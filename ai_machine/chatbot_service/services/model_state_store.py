"""
Per-team persistence for the FIBA prediction model's master state.

Replaces the old module-level MASTER_* globals (one shared blob for the whole
process) with one durable, isolated state blob per team on disk:

    data/projects/{team}/model/
        player_data.parquet   (or .pkl fallback)
        team_profiles.parquet
        pm_data.parquet
        lineup_data.parquet
        pbp_data.parquet
        model.joblib

DataFrames use parquet when an engine (pyarrow/fastparquet) is available and fall
back to a joblib pickle otherwise. The sklearn pipeline always uses joblib. All
writes are atomic (temp file + os.replace) so a crash mid-save can't corrupt state.
Serialize writes per team via TeamTaskQueue — this store does not lock.
"""
from __future__ import annotations

import os
from pathlib import Path
from typing import Any

import joblib
import pandas as pd

from services.project_store import ProjectStore

# DataFrame artifacts that make up the master dataset, in MasterState field order.
_FRAME_KEYS = ("player_data", "team_profiles", "pm_data", "lineup_data", "pbp_data")
_MODEL_KEY = "model_pipeline"


class ModelStateStore:
    def __init__(self, store: ProjectStore) -> None:
        self.store = store

    def model_dir(self, team: str) -> Path:
        d = self.store.project_dir(team) / "model"
        d.mkdir(parents=True, exist_ok=True)
        return d

    def has_state(self, team: str) -> bool:
        d = self.model_dir(team)
        return (d / "player_data.parquet").exists() or (d / "player_data.pkl").exists()

    # ── write ────────────────────────────────────────────────────────────────
    @staticmethod
    def _atomic_replace(tmp: Path, final: Path) -> None:
        os.replace(tmp, final)

    def _save_frame(self, d: Path, name: str, frame: pd.DataFrame | None) -> None:
        # Clean any stale artifact of either format first.
        for ext in (".parquet", ".pkl"):
            stale = d / f"{name}{ext}"
            if stale.exists():
                stale.unlink()
        if frame is None:
            return  # absence of file == None on load
        try:
            tmp = d / f"{name}.parquet.tmp"
            frame.to_parquet(tmp, index=False)
            self._atomic_replace(tmp, d / f"{name}.parquet")
        except Exception:  # pragma: no cover - missing parquet engine, etc.
            tmp = d / f"{name}.pkl.tmp"
            joblib.dump(frame, tmp)
            self._atomic_replace(tmp, d / f"{name}.pkl")

    def save(self, team: str, state: dict[str, Any]) -> None:
        """
        state: {player_data, team_profiles, pm_data, lineup_data, pbp_data: DataFrame|None,
                model_pipeline: sklearn Pipeline|None}
        """
        d = self.model_dir(team)
        for key in _FRAME_KEYS:
            self._save_frame(d, key, state.get(key))

        model = state.get(_MODEL_KEY)
        model_final = d / "model.joblib"
        if model is None:
            if model_final.exists():
                model_final.unlink()
        else:
            tmp = d / "model.joblib.tmp"
            joblib.dump(model, tmp)
            self._atomic_replace(tmp, model_final)

    # ── read ─────────────────────────────────────────────────────────────────
    def _load_frame(self, d: Path, name: str) -> pd.DataFrame | None:
        parquet = d / f"{name}.parquet"
        if parquet.exists():
            return pd.read_parquet(parquet)
        pkl = d / f"{name}.pkl"
        if pkl.exists():
            return joblib.load(pkl)
        return None

    def load(self, team: str) -> dict[str, Any]:
        """Return the per-team state dict; missing artifacts come back as None."""
        d = self.model_dir(team)
        state: dict[str, Any] = {key: self._load_frame(d, key) for key in _FRAME_KEYS}
        model_final = d / "model.joblib"
        state[_MODEL_KEY] = joblib.load(model_final) if model_final.exists() else None
        return state
