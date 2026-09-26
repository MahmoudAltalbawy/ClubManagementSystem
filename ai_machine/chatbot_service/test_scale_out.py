"""
Phase 5 scale-out unit tests (broker-free).

These check the pieces that can be verified without a live Redis or Celery worker:

  * The queue factory honours QUEUE_BACKEND and defaults to in-process.
  * InProcessTeamQueue serializes same-team jobs and runs different teams in parallel.
  * enqueue_ingest routes through the shared run_ingest_and_retrain body.
  * _payload_to_dict round-trips pydantic v1/v2 payloads and plain dicts.
  * The Redis-backed cache / version stamp / team lock all FALL SOFT when no Redis is
    configured (no redis import required for these to pass).
  * Importing services.celery_queue is lazy — absent only if celery isn't installed.

Run directly (``python test_scale_out.py``) or via pytest.
"""
from __future__ import annotations

import os
import sys
import threading
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from services import cache  # noqa: E402
from services.cache import RedisTTLCache, _MISS, make_read_cache  # noqa: E402
from services.redis_lock import team_lock  # noqa: E402
from services.task_runner import _payload_to_dict  # noqa: E402
from services.team_task_queue import (  # noqa: E402
    InProcessTeamQueue,
    TeamQueue,
    TeamTaskQueue,
    make_team_queue,
)


# ---------------------------------------------------------------------------
# Queue factory
# ---------------------------------------------------------------------------
def _restore_env(name: str, prev: str | None) -> None:
    if prev is None:
        os.environ.pop(name, None)
    else:
        os.environ[name] = prev


def test_factory_defaults_to_inprocess() -> None:
    prev = os.environ.get("QUEUE_BACKEND")
    os.environ.pop("QUEUE_BACKEND", None)
    try:
        q = make_team_queue()
        assert isinstance(q, InProcessTeamQueue)
        assert isinstance(q, TeamQueue)
    finally:
        _restore_env("QUEUE_BACKEND", prev)


def test_factory_unknown_backend_is_inprocess() -> None:
    prev = os.environ.get("QUEUE_BACKEND")
    os.environ["QUEUE_BACKEND"] = "bogus"
    try:
        assert isinstance(make_team_queue(), InProcessTeamQueue)
    finally:
        _restore_env("QUEUE_BACKEND", prev)


def test_celery_alias_falls_back_when_unavailable() -> None:
    # QUEUE_BACKEND=celery but no broker/celery reachable → fail-soft to in-process,
    # never raises. (If celery IS installed and a default localhost broker happens to
    # be up, a CeleryTeamQueue is acceptable too — assert we got *some* TeamQueue.)
    prev = os.environ.get("QUEUE_BACKEND")
    os.environ["QUEUE_BACKEND"] = "celery"
    try:
        q = make_team_queue()
        assert isinstance(q, TeamQueue)
    finally:
        _restore_env("QUEUE_BACKEND", prev)


def test_backward_compat_alias() -> None:
    assert TeamTaskQueue is InProcessTeamQueue


# ---------------------------------------------------------------------------
# InProcessTeamQueue serialization semantics
# ---------------------------------------------------------------------------
def test_same_team_jobs_serialize() -> None:
    q = InProcessTeamQueue()
    active = {"now": 0, "max": 0}
    lock = threading.Lock()

    def job(_tag: str) -> None:
        with lock:
            active["now"] += 1
            active["max"] = max(active["max"], active["now"])
        time.sleep(0.02)
        with lock:
            active["now"] -= 1

    futures = [q.submit("EGY", job, f"j{i}") for i in range(5)]
    for f in futures:
        f.result(timeout=5)
    # Never two jobs for the same team at once.
    assert active["max"] == 1


def test_different_teams_run_in_parallel() -> None:
    q = InProcessTeamQueue()
    start = threading.Barrier(2, timeout=5)
    overlapped = {"ok": False}

    def job() -> None:
        try:
            start.wait()  # both must arrive for this to proceed → proves concurrency
            overlapped["ok"] = True
        except threading.BrokenBarrierError:
            overlapped["ok"] = False

    f1 = q.submit("EGY", job)
    f2 = q.submit("MAR", job)
    f1.result(timeout=5)
    f2.result(timeout=5)
    assert overlapped["ok"] is True


def test_pending_count_and_submit_requires_team() -> None:
    q = InProcessTeamQueue()
    assert q.pending("NOBODY") == 0
    try:
        q.submit("", lambda: None)
        raised = False
    except ValueError:
        raised = True
    assert raised


# ---------------------------------------------------------------------------
# enqueue_ingest dispatch (default TeamQueue behaviour)
# ---------------------------------------------------------------------------
class _CapturingQueue(TeamQueue):
    """Records submit() calls so we can assert enqueue_ingest's wiring without running
    the real (heavy) ingest body."""

    def __init__(self) -> None:
        self.calls: list[tuple] = []

    def submit(self, team, fn, *args, **kwargs):
        self.calls.append((team, fn, args, kwargs))
        return None

    def pending(self, team) -> int:
        return 0


class _Payload:
    team_id = "EGY"

    def model_dump(self) -> dict:
        return {"team_id": "EGY", "match_stats_id": "m1"}


def test_enqueue_ingest_routes_through_submit() -> None:
    from services.task_runner import run_ingest_and_retrain

    q = _CapturingQueue()
    q.enqueue_ingest(_Payload())
    assert len(q.calls) == 1
    team, fn, args, _ = q.calls[0]
    assert team == "EGY"
    assert fn is run_ingest_and_retrain
    assert args == ({"team_id": "EGY", "match_stats_id": "m1"},)


def test_enqueue_ingest_requires_team_id() -> None:
    class _NoTeam:
        team_id = ""

    q = _CapturingQueue()
    try:
        q.enqueue_ingest(_NoTeam())
        raised = False
    except ValueError:
        raised = True
    assert raised


# ---------------------------------------------------------------------------
# Payload serialization (v1/v2/dict)
# ---------------------------------------------------------------------------
def test_payload_to_dict_variants() -> None:
    assert _payload_to_dict({"a": 1}) == {"a": 1}

    class V2:
        def model_dump(self):
            return {"v": 2}

    class V1:
        def dict(self):
            return {"v": 1}

    assert _payload_to_dict(V2()) == {"v": 2}
    assert _payload_to_dict(V1()) == {"v": 1}


# ---------------------------------------------------------------------------
# Redis-backed pieces fall soft without Redis
# ---------------------------------------------------------------------------
def test_make_read_cache_inprocess_without_redis() -> None:
    # No CACHE_BACKEND=redis → always the in-process TTLCache.
    prev = os.environ.get("CACHE_BACKEND")
    os.environ.pop("CACHE_BACKEND", None)
    try:
        c = make_read_cache(30)
        assert not isinstance(c, RedisTTLCache)
        c.set("k", 0, {"x": 1})
        assert c.get("k", 0) == {"x": 1}
    finally:
        _restore_env("CACHE_BACKEND", prev)


def test_version_stamp_zero_without_backend() -> None:
    # No APP_API_CACHE_DIR and no redis backend → always 0, bump is a no-op, never raises.
    prev_dir = os.environ.get("APP_API_CACHE_DIR")
    prev_backend = os.environ.get("CACHE_BACKEND")
    os.environ.pop("APP_API_CACHE_DIR", None)
    os.environ.pop("CACHE_BACKEND", None)
    try:
        assert cache.get_team_version("EGY") == 0
        cache.bump_team_version("EGY")
        assert cache.get_team_version("EGY") == 0
    finally:
        _restore_env("APP_API_CACHE_DIR", prev_dir)
        _restore_env("CACHE_BACKEND", prev_backend)


def test_team_lock_noop_without_redis() -> None:
    # No REDIS_URL → a no-op lock that always acquires and releases cleanly.
    prev_url = os.environ.get("REDIS_URL")
    prev_broker = os.environ.get("CELERY_BROKER_URL")
    os.environ.pop("REDIS_URL", None)
    os.environ.pop("CELERY_BROKER_URL", None)
    try:
        from services.redis_client import reset_redis_cache

        reset_redis_cache()
        lock = team_lock("EGY")
        assert lock.acquire(blocking=False) is True
        lock.release()  # must not raise
    finally:
        _restore_env("REDIS_URL", prev_url)
        _restore_env("CELERY_BROKER_URL", prev_broker)
        from services.redis_client import reset_redis_cache

        reset_redis_cache()


def test_redis_ttl_cache_disabled_when_ttl_zero() -> None:
    # ttl<=0 disables, matching TTLCache. Uses a dummy client so no redis needed.
    class _DummyClient:
        def get(self, *_a, **_k):
            raise AssertionError("should not be called when ttl<=0")

        def set(self, *_a, **_k):
            raise AssertionError("should not be called when ttl<=0")

    c = RedisTTLCache(0, _DummyClient())
    c.set("k", 0, {"x": 1})  # no-op
    assert c.get("k", 0) is _MISS


def test_redis_ttl_cache_roundtrip_with_fake_client() -> None:
    # In-memory fake stands in for Redis: verify version gating + None caching.
    class _FakeRedis:
        def __init__(self) -> None:
            self.store: dict[str, str] = {}

        def get(self, key):
            return self.store.get(key)

        def set(self, key, value, ex=None):
            self.store[key] = value

    c = RedisTTLCache(30, _FakeRedis())
    c.set("k", 1, {"a": 1})
    assert c.get("k", 1) == {"a": 1}
    assert c.get("k", 2) is _MISS          # version bump invalidates
    c.set("down", 0, None)
    assert c.get("down", 0) is None        # cached None is a hit, not a miss
    assert c.get("absent", 0) is _MISS


# ---- script entry point ---------------------------------------------------
def main() -> int:
    tests = [
        test_factory_defaults_to_inprocess,
        test_factory_unknown_backend_is_inprocess,
        test_celery_alias_falls_back_when_unavailable,
        test_backward_compat_alias,
        test_same_team_jobs_serialize,
        test_different_teams_run_in_parallel,
        test_pending_count_and_submit_requires_team,
        test_enqueue_ingest_routes_through_submit,
        test_enqueue_ingest_requires_team_id,
        test_payload_to_dict_variants,
        test_make_read_cache_inprocess_without_redis,
        test_version_stamp_zero_without_backend,
        test_team_lock_noop_without_redis,
        test_redis_ttl_cache_disabled_when_ttl_zero,
        test_redis_ttl_cache_roundtrip_with_fake_client,
    ]
    failures = 0
    for t in tests:
        try:
            t()
            print(f"[ok ] {t.__name__}")
        except AssertionError as exc:
            failures += 1
            print(f"[FAIL] {t.__name__}: {exc}")
        except Exception as exc:  # pragma: no cover - environment-dependent
            failures += 1
            print(f"[ERR ] {t.__name__}: {exc!r}")
    print()
    if failures:
        print(f"{failures} failure(s).")
        return 1
    print(f"All {len(tests)} Phase 5 scale-out tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
