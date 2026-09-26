"""
Per-process short-TTL read cache + cross-worker version stamps (Phase 2f / 2.5b).

Why this exists
---------------
A multi-message coaching conversation re-hits the same .NET endpoints every turn
(roster, availability, schedule, box scores). Phase 2f caches those reads for a few
tens of seconds so a back-and-forth chat stops paying the network round trip on each
message. This is the *pragmatic* version of "have our own DB" — cache reads, don't
stand up a second database.

The coherence problem (2.5b)
----------------------------
The cache is per-process. Run more than one Uvicorn/Gunicorn worker and each has its
own copy, so an ingest/rebuild in worker A would leave workers B/C serving stale data
until their TTLs expire. To bound that without a message bus, every cache entry is
tagged with a **team version stamp** read from a small file shared by all workers
(``APP_API_CACHE_DIR``). Ingest/rebuild calls ``bump_team_version(team_id)``; the next
read in *any* worker sees a higher version, treats its entry as stale, and refetches.

Multi-instance coherence (Phase 5)
----------------------------------
A shared *file* is fine for several workers on one box, but not for instances on
different hosts. With ``CACHE_BACKEND=redis`` the version stamp becomes a Redis counter
(``INCR``) shared by every instance, so an ingest on instance A is visible to B and C
immediately. The same flag also enables ``RedisTTLCache`` for the JSON app-data reads,
so the read cache itself is shared rather than per-process. (The derived DataFrame
"frame cache" stays per-process — it's a local CPU/memory optimization — but its
coherence still rides on the now-cross-instance version stamp.)

Fail-soft everywhere: if neither a cache dir nor Redis is configured the version is
always 0 (TTL-only behaviour — still correct within a worker, and cross-worker
staleness is bounded by the TTL). No cache operation ever raises into the request path.
"""
from __future__ import annotations

import json
import os
import threading
import time
from typing import Any

# Sentinel distinguishing "key absent / expired" from a legitimately cached ``None``
# (a None payload means "the app call returned nothing", which is worth caching too).
_MISS = object()


class TTLCache:
    """Tiny thread-safe TTL cache. Entries also carry a version int; a read whose
    stored version differs from the caller's current version is treated as a miss, so a
    ``bump_team_version`` invalidates without per-key bookkeeping."""

    __slots__ = ("ttl", "_store", "_lock")

    def __init__(self, ttl_seconds: float) -> None:
        self.ttl = float(ttl_seconds)
        self._store: dict[str, tuple[float, int, Any]] = {}
        self._lock = threading.Lock()

    def get(self, key: str, version: int) -> Any:
        if self.ttl <= 0:
            return _MISS
        with self._lock:
            entry = self._store.get(key)
            if entry is None:
                return _MISS
            expiry, ver, value = entry
            if ver != version or time.monotonic() > expiry:
                self._store.pop(key, None)
                return _MISS
            return value

    def set(self, key: str, version: int, value: Any) -> None:
        if self.ttl <= 0:
            return
        with self._lock:
            self._store[key] = (time.monotonic() + self.ttl, version, value)

    def clear(self) -> None:
        with self._lock:
            self._store.clear()


class RedisTTLCache:
    """Drop-in for :class:`TTLCache` backed by Redis (Phase 5).

    Same ``get(key, version)`` / ``set(key, version, value)`` / ``clear()`` surface, but
    entries live in Redis so every instance shares one cache. Values are JSON-encoded
    alongside their version stamp; a cached ``None`` round-trips as a real hit (so a
    down upstream isn't re-hit each turn). Redis sets the TTL via ``EX``.

    Fail-soft: any Redis error (or a value that isn't JSON-serializable) degrades to a
    miss/no-op rather than raising. Only suitable for JSON-serializable payloads — the
    app-data read cache stores plain dicts, so that holds.
    """

    __slots__ = ("ttl", "_client", "_prefix")

    def __init__(self, ttl_seconds: float, client: Any, prefix: str = "appcache:") -> None:
        self.ttl = float(ttl_seconds)
        self._client = client
        self._prefix = prefix

    def get(self, key: str, version: int) -> Any:
        if self.ttl <= 0:
            return _MISS
        try:
            raw = self._client.get(self._prefix + key)
        except Exception:
            return _MISS
        if raw is None:
            return _MISS
        try:
            entry = json.loads(raw)
            if int(entry.get("v", -1)) != int(version):
                return _MISS
            return entry.get("d")
        except Exception:
            return _MISS

    def set(self, key: str, version: int, value: Any) -> None:
        if self.ttl <= 0:
            return
        try:
            raw = json.dumps({"v": int(version), "d": value})
        except (TypeError, ValueError):
            return  # non-JSON payload — skip caching rather than raise
        try:
            self._client.set(self._prefix + key, raw, ex=max(int(self.ttl), 1))
        except Exception:
            return

    def clear(self) -> None:
        try:
            keys = list(self._client.scan_iter(match=self._prefix + "*"))
            if keys:
                self._client.delete(*keys)
        except Exception:
            return


def _redis_backend_enabled() -> bool:
    return os.getenv("CACHE_BACKEND", "").strip().lower() == "redis"


def make_read_cache(ttl_seconds: float):
    """Construct the configured read-cache backend.

    ``CACHE_BACKEND=redis`` (with a reachable Redis) returns a shared
    :class:`RedisTTLCache`; otherwise the per-process :class:`TTLCache`. Fail-soft: a
    missing redis client falls back to the in-process cache.
    """
    if _redis_backend_enabled():
        try:
            from services.redis_client import get_redis

            client = get_redis()
            if client is not None:
                return RedisTTLCache(ttl_seconds, client)
        except Exception:
            pass
    return TTLCache(ttl_seconds)


# ---------------------------------------------------------------------------
# Cross-worker / cross-instance team version stamps.
#   * default: a small file shared by workers on one host (``APP_API_CACHE_DIR``).
#   * CACHE_BACKEND=redis: a Redis counter shared by every instance (Phase 5).
# ---------------------------------------------------------------------------

def _version_dir() -> str | None:
    directory = os.getenv("APP_API_CACHE_DIR", "").strip()
    return directory or None


def _redis_or_none() -> Any | None:
    """Redis client iff CACHE_BACKEND=redis and a client is reachable, else None."""
    if not _redis_backend_enabled():
        return None
    try:
        from services.redis_client import get_redis

        return get_redis()
    except Exception:
        return None


def get_team_version(team_id: str) -> int:
    """Current cache-invalidation version for a team.

    Reads the Redis counter when ``CACHE_BACKEND=redis`` (shared across instances),
    otherwise the host-local file. Returns 0 when nothing is configured or the stamp
    can't be read — so caching degrades to pure TTL rather than breaking. Never raises.
    """
    if not team_id:
        return 0

    client = _redis_or_none()
    if client is not None:
        try:
            raw = client.get(f"team-version:{team_id}")
            return int(raw) if raw is not None else 0
        except Exception:
            return 0

    directory = _version_dir()
    if not directory:
        return 0
    try:
        with open(os.path.join(directory, f"{team_id}.ver"), "r", encoding="utf-8") as handle:
            return int((handle.read().strip() or "0"))
    except Exception:
        return 0


def bump_team_version(team_id: str) -> None:
    """Invalidate every worker/instance's cached reads for a team. Call from
    ingest/rebuild after new data lands. Atomic and fail-soft.

    Redis path: a single ``INCR`` (atomic across instances). File path: temp file +
    ``os.replace``.
    """
    if not team_id:
        return

    client = _redis_or_none()
    if client is not None:
        try:
            client.incr(f"team-version:{team_id}")
        except Exception:
            return
        return

    directory = _version_dir()
    if not directory:
        return
    try:
        os.makedirs(directory, exist_ok=True)
        current = get_team_version(team_id)
        path = os.path.join(directory, f"{team_id}.ver")
        tmp = f"{path}.tmp.{os.getpid()}"
        with open(tmp, "w", encoding="utf-8") as handle:
            handle.write(str(current + 1))
        os.replace(tmp, path)
    except Exception:
        return


def cache_ttl_seconds() -> float:
    """Read TTL from APP_API_CACHE_TTL (default 45s). 0/negative disables caching."""
    try:
        return float(os.getenv("APP_API_CACHE_TTL", "45"))
    except (TypeError, ValueError):
        return 45.0
