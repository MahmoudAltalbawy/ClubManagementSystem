"""
Celery application (Phase 5).

Only imported when ``QUEUE_BACKEND=celery`` (via ``services/celery_queue.py``) or by a
worker entrypoint (``worker.py``), so ``celery`` stays an optional dependency for
single-instance deploys.

Broker + result backend come from the environment, defaulting to a local Redis:

  * ``CELERY_BROKER_URL``     (fallback ``REDIS_URL``, then redis://localhost:6379/0)
  * ``CELERY_RESULT_BACKEND`` (fallback ``REDIS_URL`` db 1, then …/1)

Config notes:
  * ``acks_late`` + ``reject_on_worker_lost`` + ``prefetch_multiplier=1`` so a job a
    worker crashed on is redelivered, and a worker doesn't hoard queued jobs (which
    would defeat the per-team lock's re-queue-on-contention).
  * JSON serialization only — payloads are plain dicts, never pickled objects.
"""
from __future__ import annotations

import os

from celery import Celery

_DEFAULT_REDIS = "redis://localhost:6379"


def _broker_url() -> str:
    return (
        os.getenv("CELERY_BROKER_URL")
        or os.getenv("REDIS_URL")
        or f"{_DEFAULT_REDIS}/0"
    )


def _result_backend() -> str:
    return (
        os.getenv("CELERY_RESULT_BACKEND")
        or os.getenv("REDIS_URL")
        or f"{_DEFAULT_REDIS}/1"
    )


celery_app = Celery("chatbot", broker=_broker_url(), backend=_result_backend())
celery_app.conf.update(
    task_default_queue=os.getenv("CELERY_TASK_QUEUE", "ingest"),
    task_acks_late=True,
    task_reject_on_worker_lost=True,
    worker_prefetch_multiplier=1,
    task_serializer="json",
    result_serializer="json",
    accept_content=["json"],
    timezone="UTC",
    enable_utc=True,
    # Keep results briefly so an operator can inspect a recent job; not relied on.
    result_expires=int(os.getenv("CELERY_RESULT_EXPIRES", "3600")),
)

# Register tasks on the app. Imported at the bottom (after ``celery_app`` exists) so
# ``services/tasks.py`` can decorate against it without a circular import.
from services import tasks as _tasks  # noqa: E402,F401
