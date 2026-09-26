from __future__ import annotations

import asyncio
import hmac
import json
import logging
import os
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import Depends, FastAPI, File, HTTPException, Query, Request, UploadFile
from fastapi.responses import JSONResponse, StreamingResponse

from api.dependencies import (
    get_extraction_service,
    get_groq_client,
    get_project_store,
    get_question_service,
    get_team_queue,
    require_service_token,
)
from api.schemas import (
    AskRequest,
    AskResponse,
    HealthResponse,
    MatchStatsWebhookPayload,
    PdfUploadResponse,
    ProjectCreateRequest,
    ProjectCreateResponse,
    ProjectStatusResponse,
    RebuildResponse,
    SessionClearedResponse,
    SessionListResponse,
    SessionSummary,
    TranscriptMessage,
    TranscriptResponse,
    WebhookAcceptedResponse,
)
from embedding_utils import embed_texts
from services.extraction_service import ExtractionService
from services.groq_client import GroqClient
from services.project_store import ProjectStore
from services.question_service import QuestionService
from services.team_task_queue import TeamQueue

logger = logging.getLogger("chatbot.startup")

# Phase 2.5c readiness flag. /health is cheap liveness (always ok if the process is up);
# /ready flips true only once startup warmup has been attempted, so an orchestrator can
# hold traffic until the first request won't pay the cold model load.
_READINESS = {"ready": False}


def _warm_enabled() -> bool:
    return os.getenv("WARM_MODELS_ON_STARTUP", "1").strip().lower() in {"1", "true", "yes", "on"}


def _warm_models() -> None:
    """Force the local MiniLM embedding model to load + run once (blocking, CPU-bound)."""
    if not _warm_enabled():
        return
    embed_texts(["warmup"])


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Phase 2e/2.5c: warm the embedding model so the first real request doesn't eat the
    # multi-second cold load. Run off the event loop (to_thread) since encode() is
    # blocking, and inside try/except so a warmup failure (missing model, OOM) degrades
    # to lazy loading rather than crash-looping the process. Readiness flips true once the
    # attempt completes — success OR handled failure — so a warmup error never wedges the
    # load balancer into never routing traffic.
    try:
        await asyncio.to_thread(_warm_models)
    except Exception:  # pragma: no cover - depends on optional model/runtime
        logger.warning("model warmup failed; continuing with lazy load", exc_info=True)
    finally:
        _READINESS["ready"] = True
    yield


app = FastAPI(
    title="FIBA PDF Coach Chatbot API",
    version="1.0.0",
    description="FastAPI microservice for PDF upload, extraction, basketball analytics, and Groq-backed RAG answers.",
    lifespan=lifespan,
)


def _service_auth_enabled() -> bool:
    return os.getenv("REQUIRE_SERVICE_TOKEN", "0").strip().lower() in {"1", "true", "yes", "on"}


@app.middleware("http")
async def require_app_service_token(request: Request, call_next):
    """Protect the chatbot port when it is deployed on a separate machine.

    The backend already sends MICROSERVICE_SERVICE_TOKEN on proxied chatbot calls.
    Health probes stay public; local development remains unchanged unless the flag is set.
    """
    if not _service_auth_enabled() or request.url.path in {"/health", "/ready"}:
        return await call_next(request)

    expected = os.getenv("MICROSERVICE_SERVICE_TOKEN", "")
    if not expected:
        return JSONResponse(status_code=503, content={"detail": "Service token is not configured."})

    authorization = request.headers.get("authorization", "")
    provided = authorization[7:].strip() if authorization.lower().startswith("bearer ") else ""
    if not provided or not hmac.compare_digest(provided, expected):
        return JSONResponse(status_code=401, content={"detail": "Invalid service token."})
    return await call_next(request)


@app.get("/health", response_model=HealthResponse)
def health(groq_client: GroqClient = Depends(get_groq_client)) -> HealthResponse:
    return HealthResponse(status="ok", llm_provider="groq", groq_configured=groq_client.is_configured())


@app.get("/ready")
def ready() -> dict[str, str]:
    """Readiness probe (Phase 2.5c). 503 until startup warmup has been attempted, then
    200 — point the load balancer/orchestrator here rather than /health so traffic
    isn't routed while the model is still cold."""
    if not _READINESS["ready"]:
        raise HTTPException(status_code=503, detail="warming up")
    return {"status": "ready"}


@app.post("/projects", response_model=ProjectCreateResponse)
def create_project(
    request: ProjectCreateRequest,
    store: ProjectStore = Depends(get_project_store),
) -> ProjectCreateResponse:
    try:
        project_id = store.create_project(request.project_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return ProjectCreateResponse(project_id=project_id, status="created")


@app.post("/projects/{project_id}/pdfs", response_model=PdfUploadResponse)
async def upload_pdfs(
    project_id: str,
    files: list[UploadFile] = File(...),
    rebuild: bool = Query(False),
    store: ProjectStore = Depends(get_project_store),
    extraction: ExtractionService = Depends(get_extraction_service),
) -> PdfUploadResponse:
    try:
        store.ensure_project(project_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc

    saved_files: list[str] = []
    for upload in files:
        filename = Path(upload.filename or "").name
        if not filename.lower().endswith(".pdf"):
            raise HTTPException(status_code=400, detail=f"{filename or 'uploaded file'} is not a PDF")
        target = store.pdf_dir(project_id) / filename
        target.write_bytes(await upload.read())
        saved_files.append(filename)

    if rebuild:
        extraction.rebuild(project_id)
    return PdfUploadResponse(project_id=project_id, saved_files=saved_files, rebuild_started=rebuild)


@app.post("/projects/{project_id}/rebuild", response_model=RebuildResponse)
def rebuild_project(
    project_id: str,
    extraction: ExtractionService = Depends(get_extraction_service),
) -> RebuildResponse:
    try:
        summary = extraction.rebuild(project_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return RebuildResponse(**summary)


@app.post("/projects/{project_id}/ask", response_model=AskResponse)
def ask_project(
    project_id: str,
    request: AskRequest,
    questions: QuestionService = Depends(get_question_service),
) -> AskResponse:
    try:
        response = questions.ask(
            project_id=project_id,
            question=request.question,
            team=request.team,
            session_id=request.session_id,
            debug=request.debug,
            pdf_scope=request.pdf_scope,
        )
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:  # pragma: no cover - defensive API boundary
        logger.exception("ask_project failed")
        raise HTTPException(status_code=500, detail="internal error") from exc
    return AskResponse(**response)


def _format_sse(event: dict[str, object]) -> str:
    """Serialise one ask_stream event dict to an SSE frame: a named event plus a
    JSON ``data:`` line. The frontend switches on the event name (meta/token/done/
    error) and JSON-parses the payload."""
    name = str(event.get("event", "message"))
    payload = json.dumps(event.get("data"), ensure_ascii=False)
    return f"event: {name}\ndata: {payload}\n\n"


@app.post("/projects/{project_id}/ask/stream")
async def ask_project_stream(
    project_id: str,
    request: AskRequest,
    questions: QuestionService = Depends(get_question_service),
) -> StreamingResponse:
    """Phase 2a: SSE token streaming of an answer. Emits ``event: meta`` (ids +
    rewritten question), then ``event: token`` frames as the answer forms, then
    ``event: done`` with the full response payload, finally ``data: [DONE]``.

    ask_stream() is a blocking generator (network reads + LLM streaming), so it's
    driven one step at a time via ``asyncio.to_thread`` to keep the event loop free
    under multiple concurrent streams (Phase 2.5a). Errors are emitted as an
    ``event: error`` frame rather than tearing the connection down, since headers
    (200) are already sent once streaming starts."""
    gen = questions.ask_stream(
        project_id=project_id,
        question=request.question,
        team=request.team,
        session_id=request.session_id,
        pdf_scope=request.pdf_scope,
    )

    async def _event_source():
        sentinel = object()

        def _next():
            try:
                return next(gen)
            except StopIteration:
                return sentinel

        try:
            while True:
                item = await asyncio.to_thread(_next)
                if item is sentinel:
                    break
                yield _format_sse(item)
        except ValueError as exc:
            yield _format_sse({"event": "error", "data": {"detail": str(exc)}})
        except Exception:  # pragma: no cover - defensive, keeps the stream fail-soft
            logger.exception("ask_stream failed")
            yield _format_sse({"event": "error", "data": {"detail": "internal error"}})
        finally:
            gen.close()
            yield "data: [DONE]\n\n"

    return StreamingResponse(
        _event_source(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


@app.post(
    "/webhooks/match-stats-updated",
    response_model=WebhookAcceptedResponse,
    status_code=202,
    dependencies=[Depends(require_service_token)],
)
def match_stats_updated(
    payload: MatchStatsWebhookPayload,
    queue: TeamQueue = Depends(get_team_queue),
) -> WebhookAcceptedResponse:
    """
    Service-to-service webhook from the .NET app: a match's stats PDFs changed.
    project_id == team_id. We validate, then ENQUEUE the heavy work (pull → extract →
    retrain) on the per-team serialized queue and ack 202 immediately so the app isn't
    blocked on model training. Jobs for one team run strictly in order (in-process
    daemon thread, or — with QUEUE_BACKEND=celery — a Celery worker under a per-team
    Redis lock). The job body (pull + RAG rebuild + cache bump + retrain) lives in
    services/task_runner.run_ingest_and_retrain so both backends run identical logic.
    """
    if not payload.team_id:
        raise HTTPException(status_code=400, detail="team_id is required")

    queue.enqueue_ingest(payload)

    return WebhookAcceptedResponse(
        project_id=payload.team_id,
        match_stats_id=payload.match_stats_id,
        accepted=True,
        pulled_files=[],  # populated asynchronously; query project status to confirm
        status="queued",
    )


@app.get("/projects/{project_id}/status", response_model=ProjectStatusResponse)
def project_status(
    project_id: str,
    store: ProjectStore = Depends(get_project_store),
) -> ProjectStatusResponse:
    try:
        status = store.status(project_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return ProjectStatusResponse(**status)


@app.get("/projects/{project_id}/sessions", response_model=SessionListResponse)
def list_sessions(
    project_id: str,
    limit: int = Query(100, ge=1, le=500),
    questions: QuestionService = Depends(get_question_service),
) -> SessionListResponse:
    try:
        sessions = questions.list_sessions(project_id, limit=limit)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return SessionListResponse(
        project_id=project_id,
        sessions=[SessionSummary(**summary) for summary in sessions],
    )


@app.get("/projects/{project_id}/sessions/{session_id}", response_model=TranscriptResponse)
def session_transcript(
    project_id: str,
    session_id: str,
    questions: QuestionService = Depends(get_question_service),
) -> TranscriptResponse:
    try:
        messages = questions.get_transcript(project_id, session_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return TranscriptResponse(
        project_id=project_id,
        session_id=session_id,
        messages=[TranscriptMessage(**message) for message in messages],
    )


@app.delete("/projects/{project_id}/sessions/{session_id}", response_model=SessionClearedResponse)
def clear_session(
    project_id: str,
    session_id: str,
    questions: QuestionService = Depends(get_question_service),
) -> SessionClearedResponse:
    try:
        questions.clear_session(project_id, session_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    return SessionClearedResponse(project_id=project_id, session_id=session_id, cleared=True)


@app.post("/projects/{project_id}/sessions/{session_id}/uploads", response_model=PdfUploadResponse)
async def upload_pdfs_in_chat(
    project_id: str,
    session_id: str,
    files: list[UploadFile] = File(...),
    rebuild: bool = Query(True),
    scope: str = Query("team"),
    store: ProjectStore = Depends(get_project_store),
    extraction: ExtractionService = Depends(get_extraction_service),
    questions: QuestionService = Depends(get_question_service),
) -> PdfUploadResponse:
    """In-chat PDF upload, rebuild the RAG index so the new report is answerable
    immediately, and record a transcript marker.

    scope="team" (default): merge into the team corpus (the whole team is searchable).
    scope="session": isolate the PDF under this chat session and index it on its own,
    so a follow-up asked with pdf_scope="session" is answered strictly from this file.
    """
    scope_value = (scope or "team").strip().lower()
    if scope_value not in ("team", "session"):
        raise HTTPException(status_code=400, detail="scope must be 'team' or 'session'")

    try:
        if scope_value == "session":
            store.ensure_session(project_id, session_id)
            target_dir = store.session_pdf_dir(project_id, session_id)
        else:
            store.ensure_project(project_id)
            target_dir = store.pdf_dir(project_id)
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc

    saved_files: list[str] = []
    for upload in files:
        filename = Path(upload.filename or "").name
        if not filename.lower().endswith(".pdf"):
            raise HTTPException(status_code=400, detail=f"{filename or 'uploaded file'} is not a PDF")
        target = target_dir / filename
        target.write_bytes(await upload.read())
        saved_files.append(filename)

    if rebuild and saved_files:
        if scope_value == "session":
            extraction.rebuild_session(project_id, session_id)
        else:
            extraction.rebuild(project_id)

    if saved_files:
        where = "this chat only" if scope_value == "session" else "the team library"
        questions.record_system_message(
            project_id,
            session_id,
            f"Uploaded {len(saved_files)} PDF(s) to {where}: {', '.join(saved_files)}.",
        )

    return PdfUploadResponse(
        project_id=project_id,
        saved_files=saved_files,
        rebuild_started=bool(rebuild and saved_files),
        scope=scope_value,
        session_id=session_id,
    )
