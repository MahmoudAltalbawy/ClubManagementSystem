"""
Celery-backed implementation of ``TeamQueue`` (Phase 5).

Selected by ``QUEUE_BACKEND=celery``. Instead of running a job on a local thread, it
dispatches the registered ``chatbot.run_team_job`` task onto the Redis-backed broker;
any worker process on any instance can pick it up. Per-team serialization is enforced
by the task's Redis lock (see ``services/redis_lock.py``), not by this class.

Importing this module pulls in ``celery`` — that's intentional and only happens when
the distributed backend is actually requested (``make_team_queue`` imports it lazily).
"""
from __future__ import annotations

from typing import Any, Callable

from services.task_runner import _payload_to_dict
from services.team_task_queue import TeamQueue


class CeleryTeamQueue(TeamQueue):
    def __init__(self) -> None:
        # Import here so constructing the queue (not merely importing the module) is
        # what triggers task registration / broker config.
        from services.tasks import run_team_job

        self._run_team_job = run_team_job

    def submit(self, team: str, fn: Callable[..., Any], *args: Any, **kwargs: Any) -> Any:
        """Generic submit. The distributed backend can only dispatch *registered* tasks
        (a closure can't cross a process boundary), so ``fn`` must be a Celery task. The
        common ingest path goes through ``enqueue_ingest`` instead, which doesn't need
        the caller to know about task objects.
        """
        if not team:
            raise ValueError("team is required")
        if not hasattr(fn, "apply_async"):
            raise TypeError(
                "CeleryTeamQueue.submit requires a registered Celery task; use "
                "enqueue_ingest() for the standard webhook job."
            )
        return fn.apply_async(args=args, kwargs=kwargs)

    def enqueue_ingest(self, payload: Any) -> Any:
        """Dispatch the standard ingest+retrain job as a broker task."""
        team = getattr(payload, "team_id", None)
        if not team:
            raise ValueError("payload.team_id is required")
        return self._run_team_job.apply_async(args=[_payload_to_dict(payload)])

    def pending(self, team: str) -> int:
        # Depth is broker-managed and not cheaply introspectable per team; callers treat
        # the queue as fire-and-forget. Report 0 (unknown) rather than guess.
        return 0
