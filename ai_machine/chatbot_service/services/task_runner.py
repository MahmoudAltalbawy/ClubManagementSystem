"""
Backend-agnostic ingest+prediction job body (Phase 5).

This is the single, broker-free definition of the heavy webhook job: pull the
announced PDFs, rebuild the team's RAG index, invalidate cross-worker caches, then
retrain the team model. Both queue backends run *this same function*:

  * the in-process ``InProcessTeamQueue`` calls it directly on a per-team daemon
    thread (single-instance deploys), and
  * the Celery task (``services/tasks.run_team_job``) calls it inside a worker
    process, under a per-team Redis lock, for multi-instance deploys.

Keeping the body here — with **no celery/redis import** — means a single-instance
deploy never needs those packages installed, and the logic can't drift between the
two backends. Services are rebuilt from the cached dependency factories (they're
lru_cached singletons, so this is cheap) rather than captured in a closure, because
a Celery worker is a different process that can't receive the request's objects.
"""
from __future__ import annotations

from typing import Any


def _payload_to_dict(payload: Any) -> dict[str, Any]:
    """Serialize a webhook payload to a plain dict (pydantic v1 *or* v2, or already a
    dict). Used so the job can cross a process boundary as JSON for Celery."""
    if isinstance(payload, dict):
        return payload
    if hasattr(payload, "model_dump"):
        return payload.model_dump()
    if hasattr(payload, "dict"):
        return payload.dict()
    raise TypeError(f"Cannot serialize webhook payload of type {type(payload)!r}")


def run_ingest_and_retrain(payload_dict: dict[str, Any]) -> dict[str, Any]:
    """Pull + rebuild + invalidate + retrain for one match-stats webhook.

    Accepts a plain dict (so it survives JSON transport to a Celery worker),
    reconstructs the typed payload, and runs the exact sequence the single-instance
    webhook used to run inline. Imports are deferred to call time to keep this module
    import-light and free of FastAPI/heavy deps at queue-construction time.
    """
    # Deferred imports: avoid a heavy/circular import when this module is loaded just
    # for _payload_to_dict, and keep queue construction cheap.
    from api.dependencies import get_prediction_service, get_project_store
    from api.schemas import MatchStatsWebhookPayload
    from services.cache import bump_team_version
    from services.webhook_ingest_service import WebhookIngestService

    payload = MatchStatsWebhookPayload(**payload_dict)

    store = get_project_store()
    ingest = WebhookIngestService(store)
    prediction = get_prediction_service()

    # Pull PDFs + rebuild the per-team RAG index (box-score CSV + chunks + Chroma).
    result = ingest.ingest(payload)
    # New .NET data has landed: invalidate every worker/instance's cached reads for
    # this team (Phase 2.5b). With CACHE_BACKEND=redis the stamp is a shared Redis
    # counter, so the bump is visible to *all* instances, not just this process.
    bump_team_version(payload.team_id)
    # Ask the prediction service to pull the same backend documents and retrain last,
    # inside the same serialized job so requests for this team remain ordered.
    result["prediction"] = prediction.retrain(payload.team_id, payload=payload_dict)
    return result
