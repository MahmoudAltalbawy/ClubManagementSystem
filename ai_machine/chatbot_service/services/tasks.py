"""
Registered Celery tasks (Phase 5).

A Celery worker can't run the request-time closure the in-process queue used, so the
distributed path dispatches this *named* task with a JSON payload. The task wraps the
shared, broker-free job body (``run_ingest_and_retrain``) in a per-team Redis lock so
two jobs for the same team never overlap across worker processes/instances.

Contention handling: if another worker already holds the team's lock, we don't block a
worker slot — we ``retry`` with a short countdown, putting the job back on the broker.
Combined with ``acks_late`` (see ``celery_app``) this serializes same-team jobs while
letting different teams run in parallel.
"""
from __future__ import annotations

import os
from typing import Any

from services.celery_app import celery_app
from services.redis_lock import team_lock
from services.task_runner import run_ingest_and_retrain


def _retry_countdown() -> float:
    try:
        return float(os.getenv("TEAM_JOB_RETRY_SECONDS", "5"))
    except (TypeError, ValueError):
        return 5.0


@celery_app.task(
    bind=True,
    name="chatbot.run_team_job",
    acks_late=True,
    max_retries=None,  # keep retrying on contention; the lock TTL bounds starvation
)
def run_team_job(self, payload_dict: dict[str, Any]) -> dict[str, Any]:
    """Run ingest+retrain for one team under its distributed lock.

    Returns the same result dict as the in-process path. On lock contention the task
    re-queues itself (``retry``) instead of running concurrently with the current
    holder.
    """
    team = str(payload_dict.get("team_id") or "")
    lock = team_lock(team)
    if not lock.acquire(blocking=False):
        # Someone else holds this team — put the job back and try again shortly.
        raise self.retry(countdown=_retry_countdown())
    try:
        return run_ingest_and_retrain(payload_dict)
    finally:
        lock.release()
