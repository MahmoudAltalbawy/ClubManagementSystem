"""
Per-team distributed lock (Phase 5).

Celery has no native per-key serialization: a registered task may be picked up by any
worker slot on any instance. To keep "jobs for the SAME team never overlap" across
processes, ``run_team_job`` acquires a team-scoped Redis lock before running the job
body and releases it after. The lock is **mutual exclusion**, which is the property
that actually matters for correctness (an ingest and a retrain for one team must not
race on its model/index). Submission-order FIFO is then approximated by broker
delivery order + the task's retry-on-contention.

The lock auto-expires (``timeout``) so a crashed worker can't wedge a team forever.
Acquire is non-blocking by default: the Celery task re-queues itself on contention
rather than tying up a worker slot while it waits.

Fail-soft: with no Redis configured, ``team_lock(...)`` returns a no-op lock that
always "acquires", so behaviour collapses to the single-process case.
"""
from __future__ import annotations

import os
import uuid
from typing import Any

from services.redis_client import get_redis

# Lua: release only if we still own the lock (value matches our token). Prevents a
# worker whose lock already expired from deleting a newer holder's lock.
_RELEASE_LUA = """
if redis.call('get', KEYS[1]) == ARGV[1] then
    return redis.call('del', KEYS[1])
else
    return 0
end
"""


def _lock_ttl_ms() -> int:
    """Lock auto-expiry. Generous (default 15 min) so a long retrain holds it for the
    whole job, but bounded so a crashed worker eventually frees the team."""
    try:
        seconds = float(os.getenv("TEAM_LOCK_TTL", "900"))
    except (TypeError, ValueError):
        seconds = 900.0
    return max(int(seconds * 1000), 1000)


class _NoopLock:
    """Stand-in used when Redis is unavailable: always acquires, release is a no-op."""

    def acquire(self, blocking: bool = False) -> bool:  # noqa: D401 - trivial
        return True

    def release(self) -> None:
        return None


class _RedisTeamLock:
    """A single-owner Redis lock keyed by team, with a fencing token."""

    def __init__(self, client: Any, key: str, ttl_ms: int) -> None:
        self._client = client
        self._key = key
        self._ttl_ms = ttl_ms
        self._token = uuid.uuid4().hex
        self._held = False

    def acquire(self, blocking: bool = False) -> bool:
        # SET key token NX PX ttl  → atomic "acquire if absent, with expiry".
        try:
            ok = self._client.set(self._key, self._token, nx=True, px=self._ttl_ms)
        except Exception:
            # Redis blip: fail OPEN (treat as acquired) so ingest still runs rather than
            # being dropped. Worst case is the rare overlap the lock was avoiding.
            self._held = True
            return True
        self._held = bool(ok)
        return self._held

    def release(self) -> None:
        if not self._held:
            return
        try:
            self._client.eval(_RELEASE_LUA, 1, self._key, self._token)
        except Exception:
            pass
        finally:
            self._held = False


def team_lock(team: str) -> Any:
    """Return a lock object for ``team`` (Redis-backed if available, else a no-op).

    Usage::

        lock = team_lock(team)
        if not lock.acquire():
            ...  # contended — caller decides to retry
        try:
            ...
        finally:
            lock.release()
    """
    client = get_redis()
    if client is None or not team:
        return _NoopLock()
    return _RedisTeamLock(client, f"team-lock:{team}", _lock_ttl_ms())
