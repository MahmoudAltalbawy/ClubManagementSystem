from __future__ import annotations

import hmac
import os
from functools import lru_cache

from fastapi import Header, HTTPException

from services.extraction_service import ExtractionService
from services.groq_client import GroqClient
from services.model_state_store import ModelStateStore
from services.prediction_service import PredictionService
from services.project_store import ProjectStore
from services.question_service import QuestionService
from services.team_task_queue import TeamQueue, make_team_queue
from services.webhook_ingest_service import WebhookIngestService


@lru_cache(maxsize=1)
def get_project_store() -> ProjectStore:
    return ProjectStore()


@lru_cache(maxsize=1)
def get_groq_client() -> GroqClient:
    return GroqClient()


def get_extraction_service() -> ExtractionService:
    return ExtractionService(get_project_store())


def get_question_service() -> QuestionService:
    return QuestionService(store=get_project_store(), groq_client=get_groq_client())


@lru_cache(maxsize=1)
def get_model_state_store() -> ModelStateStore:
    return ModelStateStore(get_project_store())


@lru_cache(maxsize=1)
def get_team_queue() -> TeamQueue:
    # One process-wide queue manager, selected by QUEUE_BACKEND (in-process daemon
    # threads by default; Celery/Redis when scaling to multiple instances).
    return make_team_queue()


# Backward-compatible alias for existing imports/call sites.
get_team_task_queue = get_team_queue


@lru_cache(maxsize=1)
def get_prediction_service() -> PredictionService:
    return PredictionService(get_project_store(), get_model_state_store())


def get_webhook_ingest_service() -> WebhookIngestService:
    return WebhookIngestService(get_project_store())


def require_service_token(
    authorization: str | None = Header(default=None),
    x_service_token: str | None = Header(default=None),
) -> None:
    """
    Guard for service-to-service endpoints. Validates the shared bearer token from
    MICROSERVICE_SERVICE_TOKEN against either "Authorization: Bearer <token>" or the
    "X-Service-Token" header, using a constant-time compare. Fails closed if unset.
    """
    expected = os.getenv("MICROSERVICE_SERVICE_TOKEN", "")
    if not expected:
        raise HTTPException(status_code=503, detail="Service token is not configured.")

    provided = ""
    if authorization and authorization.lower().startswith("bearer "):
        provided = authorization[len("bearer "):].strip()
    if not provided and x_service_token:
        provided = x_service_token.strip()

    if not provided or not hmac.compare_digest(provided, expected):
        raise HTTPException(status_code=401, detail="Invalid service token.")

