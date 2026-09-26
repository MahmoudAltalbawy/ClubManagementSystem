"""Prediction facade with remote-service support and a local development fallback."""
from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Any

import pandas as pd
import requests

from services.model_state_store import ModelStateStore
from services.project_store import ProjectStore

_FIBA_ON_PATH = False
_SESSION = requests.Session()


def _ensure_fiba_on_path() -> None:
    global _FIBA_ON_PATH
    if _FIBA_ON_PATH:
        return
    configured = os.getenv("FIBA_PROJECT_DIR")
    fiba_dir = Path(configured) if configured else Path(__file__).resolve().parents[2] / "fiba_clean_project"
    fiba_dir = fiba_dir.resolve()
    if fiba_dir.is_dir() and str(fiba_dir) not in sys.path:
        sys.path.insert(0, str(fiba_dir))
    _FIBA_ON_PATH = True


class PredictionService:
    """Read and retrain predictions locally or through the prediction machine.

    Set ``PREDICTION_SERVICE_BASE_URL`` in distributed deployments. When it is
    absent, the original in-process model path remains available for local use.
    """

    def __init__(
        self,
        store: ProjectStore | None = None,
        state_store: ModelStateStore | None = None,
        base_url: str | None = None,
        service_token: str | None = None,
    ) -> None:
        self.store = store or ProjectStore()
        self.state_store = state_store or ModelStateStore(self.store)
        configured_url = base_url if base_url is not None else os.getenv("PREDICTION_SERVICE_BASE_URL", "")
        self.base_url = configured_url.strip().rstrip("/")
        self.service_token = (
            service_token
            if service_token is not None
            else os.getenv("PREDICTION_SERVICE_TOKEN", "")
        )
        self.timeout = float(os.getenv("PREDICTION_SERVICE_TIMEOUT", "900"))

    def is_remote(self) -> bool:
        return bool(self.base_url)

    def _headers(self) -> dict[str, str]:
        if not self.service_token:
            return {}
        return {"Authorization": f"Bearer {self.service_token}"}

    def predictions_csv(self, team: str) -> Path:
        return self.state_store.model_dir(team) / "test_predictions.csv"

    def retrain(self, team: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
        if self.is_remote():
            if payload is None:
                return {"team": team, "status": "remote_payload_required"}
            try:
                response = _SESSION.post(
                    f"{self.base_url}/teams/{team}/retrain",
                    json=payload,
                    headers=self._headers(),
                    timeout=(10, self.timeout),
                )
                response.raise_for_status()
                result = response.json()
                return result if isinstance(result, dict) else {"team": team, "status": "invalid_remote_response"}
            except Exception as exc:
                return {"team": team, "status": "prediction_service_unavailable", "detail": str(exc)}

        return self._retrain_local(team)

    def _retrain_local(self, team: str) -> dict[str, Any]:
        pdf_dir = self.store.pdf_dir(team)
        if not pdf_dir.exists() or not any(pdf_dir.glob("*.pdf")):
            return {"team": team, "status": "skipped_no_pdfs"}

        try:
            _ensure_fiba_on_path()
            from pipeline import bootstrap_state  # type: ignore
        except Exception as exc:
            return {"team": team, "status": "model_unavailable", "detail": str(exc)}

        model_dir = self.state_store.model_dir(team)
        output_csv = model_dir / "training_data.csv"
        output_log_csv = model_dir / "processing_log.csv"
        output_pred_csv = self.predictions_csv(team)

        try:
            summary, state = bootstrap_state(pdf_dir, output_csv, output_log_csv, output_pred_csv)
        except Exception as exc:
            return {"team": team, "status": "train_failed", "detail": str(exc)}

        self.state_store.save(
            team,
            {
                "player_data": state.player_data,
                "team_profiles": state.team_profiles,
                "pm_data": state.pm_data,
                "lineup_data": state.lineup_data,
                "pbp_data": state.pbp_data,
                "model_pipeline": state.model_pipeline,
            },
        )
        return {
            "team": team,
            "status": "trained",
            "mae": summary.get("mae"),
            "num_matches_processed": summary.get("num_matches_processed"),
            "predictions_csv": str(output_pred_csv),
            "test_prediction_rows": summary.get("test_prediction_rows"),
        }

    def load_predictions(self, team: str) -> pd.DataFrame | None:
        if self.is_remote():
            try:
                response = _SESSION.get(
                    f"{self.base_url}/teams/{team}/predictions",
                    headers=self._headers(),
                    timeout=(5, min(self.timeout, 30)),
                )
                if response.status_code == 404:
                    return None
                response.raise_for_status()
                payload = response.json()
                records = payload.get("predictions") if isinstance(payload, dict) else None
                return pd.DataFrame(records) if isinstance(records, list) else None
            except Exception:
                return None

        path = self.predictions_csv(team)
        if not path.exists():
            return None
        try:
            return pd.read_csv(path)
        except Exception:
            return None
