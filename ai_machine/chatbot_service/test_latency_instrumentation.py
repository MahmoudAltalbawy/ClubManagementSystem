"""
Phase 2 latency / hardening unit tests.

Covers the pieces added in Phase 2 that can be checked without a live .NET app or a
hosted LLM:

  * Phase 2-pre  — StageTimer: off by default (empty dict, zero overhead), records
    per-stage ms + a total when on.
  * Phase 2c     — AppDataClient shares one pooled requests.Session; default timeout is
    a (connect, read) tuple; an explicit scalar timeout still wins.
  * Phase 2f/2.5b — TTLCache honours the window, treats a version bump as invalidation,
    distinguishes a cached ``None`` from a miss, and disables when ttl<=0; the file-backed
    team version stamp increments on bump.
  * Phase 2b     — classify_question(allow_groq=False) returns the local parse WITHOUT a
    Groq round trip (the deterministic-lane fast path).

Run directly (``python test_latency_instrumentation.py``) or via pytest.
"""
from __future__ import annotations

import os
import sys
import tempfile
import time
from pathlib import Path

# Make the chatbot package root importable regardless of the caller's CWD.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import requests  # noqa: E402

from services import app_data_client  # noqa: E402
from services.app_data_client import AppDataClient, _interactive_timeout  # noqa: E402
from services.cache import (  # noqa: E402
    _MISS,
    TTLCache,
    bump_team_version,
    get_team_version,
)
from services.groq_client import GroqClient  # noqa: E402
from extract_pdfs import _atomic_write_csv  # noqa: E402
from rag_engine import _chunk_hash  # noqa: E402
from services.project_store import ProjectStore  # noqa: E402
from services.question_service import QuestionService  # noqa: E402
from services.timing import StageTimer  # noqa: E402

import pandas as pd  # noqa: E402


class _CountingGroq:
    """Minimal GroqClient stand-in that counts JSON planner calls."""

    def __init__(self, configured: bool = True) -> None:
        self.configured = configured
        self.json_calls = 0

    def is_configured(self) -> bool:
        return self.configured

    def generate_json(self, *args, **kwargs) -> dict | None:
        self.json_calls += 1
        return None

    def generate_text(self, *args, **kwargs) -> str:
        return ""


# ---------------------------------------------------------------------------
# Phase 2-pre — StageTimer
# ---------------------------------------------------------------------------
def test_timer_disabled_is_empty() -> None:
    timer = StageTimer(enabled=False)
    with timer.stage("x"):
        pass
    timer.mark("y", 5.0)
    assert timer.as_dict() == {}  # nothing leaks into prod responses


def test_timer_enabled_records_stages_and_total() -> None:
    timer = StageTimer(enabled=True)
    with timer.stage("classify"):
        time.sleep(0.005)
    timer.mark("answer", 12.5)
    out = timer.as_dict()
    assert "classify" in out and out["classify"] >= 0.0
    assert out["answer"] == 12.5
    assert "total_ms" in out


def test_timer_from_env(monkeypatch=None) -> None:
    prev = os.environ.get("DEBUG_TIMINGS")
    try:
        os.environ["DEBUG_TIMINGS"] = "1"
        assert StageTimer.from_env().enabled is True
        os.environ["DEBUG_TIMINGS"] = "0"
        assert StageTimer.from_env().enabled is False
        # An explicit override beats the env default.
        assert StageTimer.from_env(override=True).enabled is True
    finally:
        if prev is None:
            os.environ.pop("DEBUG_TIMINGS", None)
        else:
            os.environ["DEBUG_TIMINGS"] = prev


# ---------------------------------------------------------------------------
# Phase 2c — pooled session + split timeouts
# ---------------------------------------------------------------------------
def test_session_is_shared_singleton() -> None:
    assert isinstance(app_data_client._SESSION, requests.Session)
    # Re-importing the symbol yields the same object — one pool for all clients.
    from services.app_data_client import _SESSION as again
    assert again is app_data_client._SESSION


def test_default_timeout_is_connect_read_tuple() -> None:
    client = AppDataClient(base_url="http://example.invalid")
    assert isinstance(client.timeout, tuple) and len(client.timeout) == 2
    connect, read = _interactive_timeout()
    assert connect <= read


def test_explicit_scalar_timeout_wins() -> None:
    client = AppDataClient(base_url="http://example.invalid", timeout=5)
    assert client.timeout == (5.0, 5.0)


# ---------------------------------------------------------------------------
# Phase 2f / 2.5b — TTL cache + version stamp
# ---------------------------------------------------------------------------
def test_ttl_cache_hit_and_miss() -> None:
    cache = TTLCache(ttl_seconds=30)
    assert cache.get("k", version=0) is _MISS
    cache.set("k", version=0, value={"a": 1})
    assert cache.get("k", version=0) == {"a": 1}


def test_ttl_cache_caches_none_distinct_from_miss() -> None:
    cache = TTLCache(ttl_seconds=30)
    cache.set("down", version=0, value=None)
    # A cached None ("app unavailable") is a hit, not a miss — so a down app isn't
    # re-hit every turn within the window.
    assert cache.get("down", version=0) is None
    assert cache.get("absent", version=0) is _MISS


def test_ttl_cache_version_bump_invalidates() -> None:
    cache = TTLCache(ttl_seconds=30)
    cache.set("k", version=1, value="old")
    assert cache.get("k", version=1) == "old"
    # A newer version stamp (an ingest bumped it) is treated as a miss.
    assert cache.get("k", version=2) is _MISS


def test_ttl_cache_disabled_when_ttl_zero() -> None:
    cache = TTLCache(ttl_seconds=0)
    cache.set("k", version=0, value="v")
    assert cache.get("k", version=0) is _MISS


def test_team_version_stamp_file_increments() -> None:
    prev = os.environ.get("APP_API_CACHE_DIR")
    with tempfile.TemporaryDirectory() as tmp:
        os.environ["APP_API_CACHE_DIR"] = tmp
        try:
            assert get_team_version("teamA") == 0  # absent -> 0
            bump_team_version("teamA")
            assert get_team_version("teamA") == 1
            bump_team_version("teamA")
            assert get_team_version("teamA") == 2
            # Isolated per team.
            assert get_team_version("teamB") == 0
        finally:
            if prev is None:
                os.environ.pop("APP_API_CACHE_DIR", None)
            else:
                os.environ["APP_API_CACHE_DIR"] = prev


def test_team_version_zero_without_cache_dir() -> None:
    prev = os.environ.get("APP_API_CACHE_DIR")
    os.environ.pop("APP_API_CACHE_DIR", None)
    try:
        # No dir configured -> always 0 (TTL-only mode), never raises.
        assert get_team_version("teamA") == 0
        bump_team_version("teamA")  # no-op, must not raise
        assert get_team_version("teamA") == 0
    finally:
        if prev is not None:
            os.environ["APP_API_CACHE_DIR"] = prev


# ---------------------------------------------------------------------------
# Phase 2b — skip the Groq classifier when a lane matched
# ---------------------------------------------------------------------------
def _question_service() -> tuple[QuestionService, _CountingGroq]:
    tmp = tempfile.mkdtemp()
    store = ProjectStore(Path(tmp) / "projects")
    groq = _CountingGroq(configured=True)
    return QuestionService(store=store, groq_client=groq, enable_chroma_memory=False), groq


def test_classify_skips_groq_when_lane_matched() -> None:
    qs, groq = _question_service()
    parsed = qs.classify_question(
        "who is injured right now",
        pd.DataFrame(),
        [],
        default_team="EGY",
        allow_groq=False,
    )
    # No planner round trip happened, and the parse is tagged as the fast-path skip.
    assert groq.json_calls == 0
    assert parsed.get("_classification_source") == "lane_match_skip_groq"


def test_classify_lane_signal() -> None:
    # The gating signal itself: a clear lane phrase resolves to a lane, a generic
    # analytics question falls through to the classifier.
    assert QuestionService._classify_lane("who is injured") == "injuries"
    assert QuestionService._classify_lane("compare points between two players") == "classifier"


# ---------------------------------------------------------------------------
# Phase 2a — streaming SSE parse (network-free)
# ---------------------------------------------------------------------------
class _FakeStreamResponse:
    """Stand-in for a requests streaming Response: yields preset SSE lines."""

    def __init__(self, lines: list[str]) -> None:
        self._lines = lines

    def iter_lines(self, decode_unicode: bool = False):  # noqa: D401 - test shim
        yield from self._lines


def test_iter_sse_content_extracts_delta_and_stops_on_done() -> None:
    lines = [
        'data: {"choices":[{"delta":{"content":"Hel"}}]}',
        "",  # keep-alive blank, skipped
        'data: {"choices":[{"delta":{"content":"lo"}}]}',
        "data: [DONE]",
        'data: {"choices":[{"delta":{"content":"ignored after done"}}]}',
    ]
    out = list(GroqClient._iter_sse_content(_FakeStreamResponse(lines)))
    assert "".join(out) == "Hello"


def test_iter_sse_content_skips_malformed_and_non_data_lines() -> None:
    lines = [
        ": comment ping",                       # SSE comment, ignored
        "event: chunk",                          # non-data field, ignored
        "data: {not json",                       # malformed -> skipped, not fatal
        'data: {"choices":[{"delta":{}}]}',     # no content key -> skipped
        'data: {"choices":[{"delta":{"content":"ok"}}]}',
    ]
    out = list(GroqClient._iter_sse_content(_FakeStreamResponse(lines)))
    assert out == ["ok"]


# ---------------------------------------------------------------------------
# Phase 2.5d / 2.5e — atomic CSV write + content-addressed chunk hash
# ---------------------------------------------------------------------------
def test_chunk_hash_is_stable_and_content_addressed() -> None:
    a = _chunk_hash("Pau Gasol had 20 points")
    assert a == _chunk_hash("  Pau Gasol had 20 points  ")  # stripped -> identical id
    assert a != _chunk_hash("Pau Gasol had 21 points")       # different text -> different id
    assert len(a) == 40  # sha1 hexdigest


def test_atomic_write_csv_roundtrips_and_leaves_no_temp() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "box.csv"
        _atomic_write_csv(pd.DataFrame({"a": [1, 2], "b": ["x", "y"]}), path)
        back = pd.read_csv(path)
        assert list(back["a"]) == [1, 2]
        # The swap is atomic: no .tmp.* sidecar lingers next to the final file.
        leftovers = [p.name for p in Path(tmp).iterdir() if ".tmp." in p.name]
        assert leftovers == []


# ---- script entry point ---------------------------------------------------
def main() -> int:
    tests = [
        test_timer_disabled_is_empty,
        test_timer_enabled_records_stages_and_total,
        test_timer_from_env,
        test_session_is_shared_singleton,
        test_default_timeout_is_connect_read_tuple,
        test_explicit_scalar_timeout_wins,
        test_ttl_cache_hit_and_miss,
        test_ttl_cache_caches_none_distinct_from_miss,
        test_ttl_cache_version_bump_invalidates,
        test_ttl_cache_disabled_when_ttl_zero,
        test_team_version_stamp_file_increments,
        test_team_version_zero_without_cache_dir,
        test_classify_skips_groq_when_lane_matched,
        test_classify_lane_signal,
        test_iter_sse_content_extracts_delta_and_stops_on_done,
        test_iter_sse_content_skips_malformed_and_non_data_lines,
        test_chunk_hash_is_stable_and_content_addressed,
        test_atomic_write_csv_roundtrips_and_leaves_no_temp,
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
    print(f"All {len(tests)} Phase 2 latency tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
