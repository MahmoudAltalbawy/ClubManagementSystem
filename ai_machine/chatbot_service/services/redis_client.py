"""
Shared Redis connection helpers (Phase 5).

A single place that resolves the Redis URL and hands out a lazily-created, process-
wide client. Everything here is **fail-soft**: if ``redis`` isn't installed or the URL
isn't configured, ``get_redis()`` returns ``None`` and callers degrade to their
non-distributed behaviour (in-process queue, file/in-proc cache). Nothing here ever
raises into the request path.
"""
from __future__ import annotations

import os
import threading
from typing import Any

_client: Any | None = None
_client_lock = threading.Lock()
_resolved = False


def redis_url() -> str | None:
    """Resolve the Redis URL from env. Prefers REDIS_URL, then CELERY_BROKER_URL."""
    url = (os.getenv("REDIS_URL") or os.getenv("CELERY_BROKER_URL") or "").strip()
    return url or None


def get_redis() -> Any | None:
    """Return a process-wide Redis client, or ``None`` if unavailable.

    Built once and cached. A failed connection import/handshake caches ``None`` for the
    process so we don't retry the import on every call. ``decode_responses=True`` so
    values come back as ``str`` (we store JSON text).
    """
    global _client, _resolved
    if _resolved:
        return _client
    with _client_lock:
        if _resolved:
            return _client
        _resolved = True
        url = redis_url()
        if not url:
            _client = None
            return None
        try:
            import redis  # type: ignore

            client = redis.Redis.from_url(url, decode_responses=True)
            # Touch the connection so a dead broker fails here (fail-soft) instead of
            # on the first real read in a hot path.
            client.ping()
            _client = client
        except Exception:
            _client = None
        return _client


def reset_redis_cache() -> None:
    """Test hook: drop the memoized client so the next call re-resolves the env."""
    global _client, _resolved
    with _client_lock:
        _client = None
        _resolved = False
