"""
Per-team serialized task queue (Phase 5: pluggable backend).

The webhook handler returns 202 immediately and enqueues the heavy work (pull →
extract → retrain → persist). Jobs for the SAME team run strictly one-at-a-time so
an ingest and a retrain can't race on that team's model state; jobs for DIFFERENT
teams run concurrently.

Two backends implement the same ``TeamQueue`` contract:

  * ``InProcessTeamQueue`` (default) — one daemon worker thread per team with a FIFO
    queue, created lazily on first submit. Simple, dependency-free, and *correct for a
    single instance only* (its per-team ordering is in-process).
  * ``CeleryTeamQueue`` (``QUEUE_BACKEND=celery``) — enqueues onto a Redis-backed
    Celery broker; a per-team Redis lock holds serialization across multiple worker
    processes/instances. See ``services/celery_queue.py``.

Pick the backend with ``make_team_queue()`` (reads ``QUEUE_BACKEND``). The Celery
backend is imported lazily, so a single-instance deploy never needs celery/redis
installed.

``TeamTaskQueue`` remains as a backward-compatible alias for ``InProcessTeamQueue``.
"""
from __future__ import annotations

import logging
import os
import threading
from abc import ABC, abstractmethod
from concurrent.futures import Future
from queue import Queue
from typing import Any, Callable

from services.task_runner import run_ingest_and_retrain

_log = logging.getLogger("team_task_queue")


_SHUTDOWN = object()


class TeamQueue(ABC):
    """Contract for a per-team serialized job queue.

    Concrete backends guarantee: jobs submitted for the *same* team never run
    concurrently; jobs for *different* teams may run in parallel.
    """

    @abstractmethod
    def submit(self, team: str, fn: Callable[..., Any], *args: Any, **kwargs: Any) -> Any:
        """Enqueue a job for ``team``. Returns a backend-specific handle (a Future for
        in-process, an AsyncResult for Celery) that the caller may ignore."""

    @abstractmethod
    def pending(self, team: str) -> int:
        """Best-effort count of not-yet-started jobs for ``team`` (0 if unknown)."""

    def enqueue_ingest(self, payload: Any) -> Any:
        """Enqueue the standard ingest+retrain job for a match-stats webhook.

        Backend-portable entrypoint used by the webhook so the route doesn't care which
        backend is active. The default runs the shared ``run_ingest_and_retrain`` body
        on this backend's ``submit``; the Celery backend overrides it to dispatch a
        registered task instead (a closure can't cross a process boundary).
        """
        from services.task_runner import _payload_to_dict

        team = getattr(payload, "team_id", None)
        if not team:
            raise ValueError("payload.team_id is required")
        return self.submit(team, run_ingest_and_retrain, _payload_to_dict(payload))


class InProcessTeamQueue(TeamQueue):
    """In-process per-team FIFO of daemon worker threads. Correct for ONE instance."""

    def __init__(self) -> None:
        self._queues: dict[str, "Queue[Any]"] = {}
        self._threads: dict[str, threading.Thread] = {}
        self._lock = threading.Lock()

    def _ensure_worker(self, team: str) -> "Queue[Any]":
        with self._lock:
            q = self._queues.get(team)
            if q is not None:
                return q
            q = Queue()
            self._queues[team] = q
            t = threading.Thread(target=self._worker, args=(team, q), name=f"team-{team}", daemon=True)
            self._threads[team] = t
            t.start()
            return q

    @staticmethod
    def _worker(team: str, q: "Queue[Any]") -> None:
        while True:
            item = q.get()
            try:
                if item is _SHUTDOWN:
                    return
                fn, args, kwargs, future = item
                if future.set_running_or_notify_cancel():
                    try:
                        future.set_result(fn(*args, **kwargs))
                    except Exception as exc:  # surface to the caller via the Future
                        _log.warning("Team %s job failed: %s", team, exc)
                        future.set_exception(exc)
            finally:
                q.task_done()

    def submit(self, team: str, fn: Callable[..., Any], *args: Any, **kwargs: Any) -> "Future[Any]":
        """Enqueue a job for `team`. Returns a Future the caller may ignore (fire-and-forget)."""
        if not team:
            raise ValueError("team is required")
        future: "Future[Any]" = Future()
        q = self._ensure_worker(team)
        q.put((fn, args, kwargs, future))
        return future

    def pending(self, team: str) -> int:
        q = self._queues.get(team)
        return q.qsize() if q is not None else 0


# Backward-compatible alias: existing imports/tests refer to TeamTaskQueue.
TeamTaskQueue = InProcessTeamQueue


def _backend_name() -> str:
    return os.getenv("QUEUE_BACKEND", "inprocess").strip().lower()


def make_team_queue() -> TeamQueue:
    """Construct the configured queue backend.

    ``QUEUE_BACKEND=celery`` (aliases: ``redis``, ``distributed``) selects the
    distributed backend; anything else (default) stays in-process. The Celery module
    is imported only on demand, so single-instance deploys don't require celery/redis.
    """
    backend = _backend_name()
    if backend in {"celery", "redis", "distributed"}:
        try:
            from services.celery_queue import CeleryTeamQueue

            return CeleryTeamQueue()
        except Exception as exc:  # missing celery/redis, bad broker URL, etc.
            _log.warning(
                "QUEUE_BACKEND=%s requested but the Celery backend is unavailable (%s); "
                "falling back to the in-process queue.",
                backend,
                exc,
            )
            return InProcessTeamQueue()
    return InProcessTeamQueue()
