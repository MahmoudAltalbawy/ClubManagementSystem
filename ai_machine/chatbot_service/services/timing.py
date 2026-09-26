"""
Lightweight per-stage latency timing (Phase 2-pre).

The point of this module is to *measure before optimizing*: a single ``/ask`` call
fans out across several stages (semantic-memory retrieval, follow-up rewrite, the Groq
classifier, box-score load, retrieval, final LLM generation) and we want to know which
one actually dominates on a given machine before touching Phases 2a-2f.

Design constraints:
  * **Zero overhead when off.** Timing is gated by ``DEBUG_TIMINGS`` (env, default off).
    When off, ``StageTimer.stage(...)`` still works but does the minimum and nothing is
    logged or returned, so production isn't paying for instrumentation it won't read.
  * **Fail-soft.** Timing must never change an answer or raise into the request path; a
    bug in a timer is swallowed, mirroring the rest of the service's fail-soft contract.
  * **No new dependencies.** ``time.perf_counter`` + stdlib ``logging`` only.

Usage::

    timer = StageTimer.from_env()                # or StageTimer(enabled=True)
    with timer.stage("classify"):
        parsed = self.classify_question(...)
    ...
    response["timings"] = timer.as_dict()        # only when timer.enabled
"""
from __future__ import annotations

import logging
import os
import time
from contextlib import contextmanager
from typing import Iterator

logger = logging.getLogger("chatbot.timing")


def _env_truthy(name: str, default: str = "0") -> bool:
    return os.getenv(name, default).strip().lower() in {"1", "true", "yes", "on"}


class StageTimer:
    """Accumulates elapsed milliseconds per named stage.

    A single timer instance lives for the duration of one request. Stages may repeat
    (e.g. two retrieval passes); repeated names accumulate rather than overwrite, so the
    reported number is total time spent in that stage across the request.
    """

    __slots__ = ("enabled", "_stages", "_order", "_t0")

    def __init__(self, enabled: bool = False) -> None:
        self.enabled = bool(enabled)
        self._stages: dict[str, float] = {}
        self._order: list[str] = []
        self._t0 = time.perf_counter()

    @classmethod
    def from_env(cls, override: bool | None = None) -> "StageTimer":
        """Build a timer enabled by the ``DEBUG_TIMINGS`` env var.

        ``override`` lets a caller force timing on for a single request (e.g. a
        ``debug=True`` API call) regardless of the env default.
        """
        enabled = _env_truthy("DEBUG_TIMINGS") if override is None else bool(override)
        return cls(enabled=enabled)

    @contextmanager
    def stage(self, name: str) -> Iterator[None]:
        """Time the wrapped block under ``name``. A no-op (but still runs the block)
        when timing is disabled, so call sites stay identical in both modes."""
        if not self.enabled:
            yield
            return
        start = time.perf_counter()
        try:
            yield
        finally:
            try:
                elapsed_ms = (time.perf_counter() - start) * 1000.0
                if name not in self._stages:
                    self._stages[name] = 0.0
                    self._order.append(name)
                self._stages[name] += elapsed_ms
            except Exception:  # pragma: no cover - timing must never break a request
                pass

    def mark(self, name: str, elapsed_ms: float) -> None:
        """Record a pre-measured duration (for stages timed outside a ``with`` block)."""
        if not self.enabled:
            return
        try:
            if name not in self._stages:
                self._stages[name] = 0.0
                self._order.append(name)
            self._stages[name] += float(elapsed_ms)
        except Exception:  # pragma: no cover
            pass

    def total_ms(self) -> float:
        return (time.perf_counter() - self._t0) * 1000.0

    def as_dict(self) -> dict[str, float]:
        """Insertion-ordered stage→ms map plus a ``total_ms`` wall-clock key, each
        rounded to 0.1ms. Empty dict when disabled, so callers can attach it
        unconditionally without leaking an empty field into prod responses."""
        if not self.enabled:
            return {}
        out = {name: round(self._stages[name], 1) for name in self._order}
        out["total_ms"] = round(self.total_ms(), 1)
        return out

    def log(self, context: str = "") -> None:
        """Emit one structured log line with the per-stage breakdown. No-op when
        disabled. Best-effort: a logging failure never propagates."""
        if not self.enabled:
            return
        try:
            parts = [f"{name}={self._stages[name]:.1f}ms" for name in self._order]
            parts.append(f"total={self.total_ms():.1f}ms")
            suffix = f" [{context}]" if context else ""
            logger.info("stage-timings%s %s", suffix, " ".join(parts))
        except Exception:  # pragma: no cover
            pass
