from __future__ import annotations

from typing import Any

from pydantic import BaseModel, Field


class HealthResponse(BaseModel):
    status: str
    llm_provider: str
    groq_configured: bool


class ProjectCreateRequest(BaseModel):
    project_id: str | None = None


class ProjectCreateResponse(BaseModel):
    project_id: str
    status: str


class PdfUploadResponse(BaseModel):
    project_id: str
    saved_files: list[str]
    rebuild_started: bool
    scope: str = "team"
    session_id: str | None = None


class RebuildResponse(BaseModel):
    project_id: str
    status: str
    pdf_count: int
    player_rows: int
    chunk_rows: int
    vector_index: str


class AskRequest(BaseModel):
    question: str = Field(..., min_length=1)
    team: str = "EGY"
    session_id: str | None = None
    debug: bool = False
    # "team" (default): answer from the team's full corpus + live DB + model.
    # "session": answer strictly from PDFs uploaded to THIS chat session.
    pdf_scope: str = "team"


class AskResponse(BaseModel):
    project_id: str
    session_id: str
    answer: str
    type: str
    route: str
    metric: str | None = None
    metrics: list[str] = []
    players: list[str] = []
    team: str | None = None
    top_n: int | None = None
    strategy: str | None = None
    analytics_recipe: dict[str, Any] | None = None
    sources: list[Any] = []
    original_question: str
    rewritten_question: str
    retrieval_engine: str | None = None
    classification_source: str


class ProjectStatusResponse(BaseModel):
    project_id: str
    pdf_count: int
    has_box_score_csv: bool
    has_chunks_csv: bool
    has_chroma_index: bool
    has_predictions_csv: bool
    status: str


# ── Readable chat history ────────────────────────────────────────────────────
class SessionSummary(BaseModel):
    session_id: str
    message_count: int
    started_at: str
    last_at: str
    title: str = ""


class SessionListResponse(BaseModel):
    project_id: str
    sessions: list[SessionSummary] = []


class TranscriptMessage(BaseModel):
    role: str
    content: str
    route: str | None = None
    metric: str | None = None
    players: list[str] = []
    timestamp: str


class TranscriptResponse(BaseModel):
    project_id: str
    session_id: str
    messages: list[TranscriptMessage] = []


class SessionClearedResponse(BaseModel):
    project_id: str
    session_id: str
    cleared: bool


# ── App → microservice webhook (match stats updated) ─────────────────────────
# The .NET app sends snake_case JSON (set explicitly on its DTOs), so these models
# need no aliases and work on both pydantic v1 and v2.
class WebhookDocumentRef(BaseModel):
    """One stored PDF the app is offering for pull-back."""
    pdf_type: str
    pull_url: str
    file_name: str = ""


class MatchStatsWebhookPayload(BaseModel):
    """
    Posted by the .NET app when a match's stats PDFs change. project_id on this
    side == team_id. We pull file bytes from the signed pull-URLs; the app's
    box-score text is canonical and trusted verbatim.
    """
    team_id: str
    event_id: str
    match_stats_id: str
    box_score_text: str | None = None
    documents: list[WebhookDocumentRef] = Field(default_factory=list)


class WebhookAcceptedResponse(BaseModel):
    project_id: str
    match_stats_id: str
    accepted: bool
    pulled_files: list[str] = []
    status: str
