from __future__ import annotations

import hmac
import json
import math
import os
import re
import sys
import threading
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import joblib
import pandas as pd
import requests
from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel, Field


DATA_ROOT = Path(os.getenv("PREDICTION_DATA_ROOT", "/data/teams"))
FIBA_PROJECT_DIR = Path(os.getenv("FIBA_PROJECT_DIR", "/app/fiba_clean_project"))
TEAM_ID_RE = re.compile(r"^[A-Za-z0-9_.-]+$")
REPORT_TYPE_PHRASE = {
    "box_score": "FIBA Box Score",
    "plus_minus": "Player PlusMinus Summary",
    "lineup": "Line Up Analysis",
    "play_by_play": "Play by Play",
}
SAFE_SLUG_RE = re.compile(r"[^A-Za-z0-9 _.-]+")
SESSION = requests.Session()
TRAINING_SLOTS = threading.Semaphore(max(1, int(os.getenv("MAX_CONCURRENT_TRAININGS", "1"))))
TEAM_LOCKS: dict[str, threading.Lock] = {}
TEAM_LOCKS_GUARD = threading.Lock()


class WebhookDocumentRef(BaseModel):
    pdf_type: str
    pull_url: str
    file_name: str = ""


class RetrainRequest(BaseModel):
    team_id: str
    event_id: str
    match_stats_id: str
    box_score_text: str | None = None
    documents: list[WebhookDocumentRef] = Field(default_factory=list)


def require_service_token(
    authorization: str | None = Header(default=None),
    x_service_token: str | None = Header(default=None),
) -> None:
    expected = os.getenv("PREDICTION_SERVICE_TOKEN", "")
    if not expected:
        raise HTTPException(status_code=503, detail="Prediction service token is not configured.")
    provided = ""
    if authorization and authorization.lower().startswith("bearer "):
        provided = authorization[7:].strip()
    if not provided and x_service_token:
        provided = x_service_token.strip()
    if not provided or not hmac.compare_digest(provided, expected):
        raise HTTPException(status_code=401, detail="Invalid service token.")


def validate_team_id(team_id: str) -> str:
    clean = str(team_id or "").strip()
    if not clean or not TEAM_ID_RE.fullmatch(clean):
        raise HTTPException(status_code=400, detail="Invalid team id.")
    return clean


def team_dir(team_id: str) -> Path:
    path = DATA_ROOT / validate_team_id(team_id)
    path.mkdir(parents=True, exist_ok=True)
    return path


def pdf_dir(team_id: str) -> Path:
    path = team_dir(team_id) / "pdfs"
    path.mkdir(parents=True, exist_ok=True)
    return path


def model_dir(team_id: str) -> Path:
    path = team_dir(team_id) / "model"
    path.mkdir(parents=True, exist_ok=True)
    return path


def prediction_path(team_id: str) -> Path:
    return model_dir(team_id) / "test_predictions.csv"


def _team_lock(team_id: str) -> threading.Lock:
    with TEAM_LOCKS_GUARD:
        return TEAM_LOCKS.setdefault(team_id, threading.Lock())


def _stored_filename(document: WebhookDocumentRef, match_stats_id: str) -> str:
    token = document.pdf_type.strip().lower()
    phrase = REPORT_TYPE_PHRASE.get(token)
    safe_id = SAFE_SLUG_RE.sub("", match_stats_id) or "match"
    if phrase:
        return f"{phrase} {safe_id}.pdf"
    safe_token = SAFE_SLUG_RE.sub("", token) or "document"
    return f"{safe_token} {safe_id}.pdf"


def _validate_pull_url(url: str) -> None:
    configured = os.getenv("BACKEND_BASE_URL", "").strip().rstrip("/")
    if not configured:
        raise HTTPException(status_code=503, detail="BACKEND_BASE_URL is not configured.")
    expected = urlparse(configured)
    actual = urlparse(url)
    if actual.scheme not in {"http", "https"} or actual.netloc != expected.netloc:
        raise HTTPException(status_code=400, detail="Document pull URL is not on the configured backend host.")


def _pull_documents(request: RetrainRequest) -> list[str]:
    destination = pdf_dir(request.team_id)
    token = os.getenv("BACKEND_SERVICE_TOKEN", "")
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    timeout = float(os.getenv("BACKEND_PULL_TIMEOUT", "60"))
    max_bytes = int(os.getenv("MAX_PDF_BYTES", str(50 * 1024 * 1024)))
    pulled: list[str] = []

    for document in request.documents:
        _validate_pull_url(document.pull_url)
        filename = _stored_filename(document, request.match_stats_id)
        target = destination / filename
        temporary = target.with_suffix(".pdf.tmp")
        total = 0
        try:
            with SESSION.get(document.pull_url, headers=headers, timeout=(10, timeout), stream=True) as response:
                response.raise_for_status()
                with temporary.open("wb") as output:
                    for chunk in response.iter_content(chunk_size=1024 * 1024):
                        if not chunk:
                            continue
                        total += len(chunk)
                        if total > max_bytes:
                            raise ValueError("PDF exceeds MAX_PDF_BYTES")
                        output.write(chunk)
            os.replace(temporary, target)
        except Exception as exc:
            temporary.unlink(missing_ok=True)
            raise HTTPException(status_code=502, detail=f"Could not pull {filename}: {exc}") from exc
        pulled.append(filename)
    return pulled


def _ensure_model_imports() -> None:
    fiba_dir = FIBA_PROJECT_DIR.resolve()
    if not fiba_dir.is_dir():
        raise RuntimeError(f"FIBA model directory does not exist: {fiba_dir}")
    if str(fiba_dir) not in sys.path:
        sys.path.insert(0, str(fiba_dir))


def _save_state(team_id: str, state: Any) -> None:
    target = model_dir(team_id) / "model_state.joblib"
    temporary = target.with_suffix(".joblib.tmp")
    joblib.dump(
        {
            "player_data": state.player_data,
            "team_profiles": state.team_profiles,
            "pm_data": state.pm_data,
            "lineup_data": state.lineup_data,
            "pbp_data": state.pbp_data,
            "model_pipeline": state.model_pipeline,
        },
        temporary,
    )
    os.replace(temporary, target)


def _finite(value: Any) -> Any:
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def _train(team_id: str) -> dict[str, Any]:
    source = pdf_dir(team_id)
    if not any(source.glob("*.pdf")):
        return {"team": team_id, "status": "skipped_no_pdfs"}

    _ensure_model_imports()
    from pipeline import bootstrap_state  # type: ignore

    output = model_dir(team_id)
    summary, state = bootstrap_state(
        source,
        output / "training_data.csv",
        output / "processing_log.csv",
        output / "test_predictions.csv",
    )
    _save_state(team_id, state)
    return {
        "team": team_id,
        "status": "trained",
        "mae": _finite(summary.get("mae")),
        "num_matches_processed": summary.get("num_matches_processed"),
        "test_prediction_rows": summary.get("test_prediction_rows"),
    }


app = FastAPI(
    title="FIBA Prediction Service",
    version="1.0.0",
    description="Authenticated per-team model training and prediction storage.",
)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "service": "prediction"}


@app.get("/ready")
def ready() -> dict[str, str]:
    DATA_ROOT.mkdir(parents=True, exist_ok=True)
    if not FIBA_PROJECT_DIR.is_dir():
        raise HTTPException(status_code=503, detail="FIBA model files are unavailable.")
    return {"status": "ready"}


@app.post("/teams/{team_id}/retrain", dependencies=[Depends(require_service_token)])
def retrain(team_id: str, request: RetrainRequest) -> dict[str, Any]:
    clean_team = validate_team_id(team_id)
    if clean_team != validate_team_id(request.team_id):
        raise HTTPException(status_code=400, detail="Path team id does not match payload team id.")

    with _team_lock(clean_team):
        pulled = _pull_documents(request)
        try:
            with TRAINING_SLOTS:
                result = _train(clean_team)
        except Exception as exc:
            raise HTTPException(status_code=500, detail=f"Model training failed: {exc}") from exc
    result["pulled_files"] = pulled
    return result


@app.get("/teams/{team_id}/predictions", dependencies=[Depends(require_service_token)])
def predictions(team_id: str) -> dict[str, Any]:
    path = prediction_path(team_id)
    if not path.exists():
        raise HTTPException(status_code=404, detail="No predictions are available for this team.")
    try:
        frame = pd.read_csv(path)
        records = json.loads(frame.to_json(orient="records", date_format="iso"))
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"Could not read predictions: {exc}") from exc
    return {"team": team_id, "count": len(records), "predictions": records}


@app.get("/teams/{team_id}/status", dependencies=[Depends(require_service_token)])
def status(team_id: str) -> dict[str, Any]:
    predictions_file = prediction_path(team_id)
    return {
        "team": validate_team_id(team_id),
        "pdf_count": len(list(pdf_dir(team_id).glob("*.pdf"))),
        "has_model": (model_dir(team_id) / "model_state.joblib").exists(),
        "has_predictions": predictions_file.exists(),
    }
