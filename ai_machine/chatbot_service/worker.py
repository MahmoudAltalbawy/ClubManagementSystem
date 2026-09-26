"""
Celery worker entrypoint (Phase 5).

Run a background worker that drains ingest/retrain jobs from the Redis-backed broker:

    celery -A worker.celery_app worker --loglevel=info --concurrency=4

Different teams run in parallel across the worker pool; jobs for the *same* team are
serialized by a per-team Redis lock (see services/redis_lock.py). Scale out by running
more of these (more processes or more containers) against the same broker.

Importing this module imports ``services.celery_app``, which registers the tasks. Only
meaningful when ``QUEUE_BACKEND=celery`` and a broker is configured; the API process
itself does not need to import this file.
"""
from __future__ import annotations

from services.celery_app import celery_app  # noqa: F401  (referenced by `celery -A worker.celery_app`)

__all__ = ["celery_app"]
