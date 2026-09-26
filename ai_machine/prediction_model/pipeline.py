from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd
from sklearn.metrics import mean_absolute_error
from sklearn.pipeline import Pipeline

from config import logger
from extractors import discover_pdf_files, extract_all_pdfs
from features import clean_player_data, engineer_features, split_train_test_by_match
from model import export_csv, refine_cold_start_predictions, train_model

# ─────────────────────────────────────────────────────────────────────────────
# Explicit, picklable per-tenant model state.
#
# The module-level MASTER_* globals below are kept ONLY for backward compatibility
# with the single-process CLI and existing tests. They are NOT safe for the
# multi-team microservice (one process serving many teams would cross-contaminate
# and race). The microservice uses MasterState + retrain_state/bootstrap_state,
# persists one MasterState per team (joblib/parquet), and serializes writes per
# team via a task queue. The global functions just delegate and then snapshot.
# ─────────────────────────────────────────────────────────────────────────────


@dataclass
class MasterState:
    """All artifacts needed to retrain/predict for one tenant (team)."""
    player_data: pd.DataFrame | None = None
    team_profiles: pd.DataFrame | None = None
    model_pipeline: Pipeline | None = None
    pm_data: pd.DataFrame | None = None
    lineup_data: pd.DataFrame | None = None
    pbp_data: pd.DataFrame | None = None

    def is_initialized(self) -> bool:
        return self.player_data is not None and self.team_profiles is not None


def _split_new_match_data(
    new_match_data: pd.DataFrame | dict[str, Any],
) -> tuple[pd.DataFrame, pd.DataFrame, pd.DataFrame, pd.DataFrame, pd.DataFrame]:
    if isinstance(new_match_data, dict):
        return (
            pd.DataFrame(new_match_data.get('players', [])),
            pd.DataFrame(new_match_data.get('team_profiles', [])),
            pd.DataFrame(new_match_data.get('pm', [])),
            pd.DataFrame(new_match_data.get('lineup', [])),
            pd.DataFrame(new_match_data.get('pbp', [])),
        )
    return (pd.DataFrame(new_match_data), pd.DataFrame(), pd.DataFrame(), pd.DataFrame(), pd.DataFrame())


def retrain_state(
    state: MasterState,
    new_match_data: pd.DataFrame | dict[str, Any],
) -> tuple[Pipeline, pd.DataFrame, MasterState]:
    """
    Pure (no globals) retrain. Returns the trained model, the engineered feature
    frame, and a NEW MasterState. The microservice persists the returned state.
    """
    if not state.is_initialized():
        raise RuntimeError('Master dataset is not initialized. Bootstrap the team first.')

    new_players, new_team, new_pm, new_lineup, new_pbp = _split_new_match_data(new_match_data)

    combined_players = pd.concat([state.player_data, new_players], ignore_index=True, sort=False)
    combined_team = pd.concat([state.team_profiles, new_team], ignore_index=True, sort=False)
    combined_pm = pd.concat([state.pm_data, new_pm], ignore_index=True, sort=False) if state.pm_data is not None else new_pm
    combined_lineup = pd.concat([state.lineup_data, new_lineup], ignore_index=True, sort=False) if state.lineup_data is not None else new_lineup
    combined_pbp = pd.concat([state.pbp_data, new_pbp], ignore_index=True, sort=False) if state.pbp_data is not None else new_pbp

    combined_players, _ = clean_player_data(combined_players)
    featured, _ = engineer_features(combined_players, combined_team, combined_pm, combined_lineup, combined_pbp)
    train_df, test_df = split_train_test_by_match(featured, 0.8)
    model, _, _, _ = train_model(train_df, test_df)

    new_state = MasterState(
        player_data=combined_players,
        team_profiles=combined_team,
        model_pipeline=model,
        pm_data=combined_pm,
        lineup_data=combined_lineup,
        pbp_data=combined_pbp,
    )
    return model, featured, new_state


# ── Backward-compatible global singleton (CLI/tests only) ────────────────────
MASTER_PLAYER_DATA: pd.DataFrame | None = None
MASTER_TEAM_PROFILES: pd.DataFrame | None = None
MASTER_MODEL_PIPELINE: Pipeline | None = None
MASTER_PM_DATA: pd.DataFrame | None = None
MASTER_LINEUP_DATA: pd.DataFrame | None = None
MASTER_PBP_DATA: pd.DataFrame | None = None


def _global_state() -> MasterState:
    return MasterState(
        player_data=MASTER_PLAYER_DATA,
        team_profiles=MASTER_TEAM_PROFILES,
        model_pipeline=MASTER_MODEL_PIPELINE,
        pm_data=MASTER_PM_DATA,
        lineup_data=MASTER_LINEUP_DATA,
        pbp_data=MASTER_PBP_DATA,
    )


def _assign_globals(state: MasterState) -> None:
    global MASTER_PLAYER_DATA, MASTER_TEAM_PROFILES, MASTER_MODEL_PIPELINE
    global MASTER_PM_DATA, MASTER_LINEUP_DATA, MASTER_PBP_DATA
    MASTER_PLAYER_DATA = state.player_data
    MASTER_TEAM_PROFILES = state.team_profiles
    MASTER_MODEL_PIPELINE = state.model_pipeline
    MASTER_PM_DATA = state.pm_data
    MASTER_LINEUP_DATA = state.lineup_data
    MASTER_PBP_DATA = state.pbp_data


def retrain_model(new_match_data: pd.DataFrame | dict[str, Any]) -> tuple[Pipeline, pd.DataFrame]:
    """Backward-compatible wrapper over retrain_state using the module globals."""
    model, featured, new_state = retrain_state(_global_state(), new_match_data)
    _assign_globals(new_state)
    return model, featured


def bootstrap_state(
    data_dir: Path, output_csv: Path, output_log_csv: Path, output_pred_csv: Path,
) -> tuple[dict[str, Any], MasterState]:
    """
    Globals-free bootstrap: extract + train from a directory of PDFs and return
    (summary, MasterState). The microservice persists the returned state per team.
    """
    # --- MODIFIED: now returns 6 DataFrames instead of 3 ---
    raw_df, team_df, log_df, pm_df, lineup_df, pbp_df = extract_all_pdfs(data_dir)
    clean_df, clean_sum = clean_player_data(raw_df)
    # --- MODIFIED: pass supplementary DFs to engineer_features ---
    featured, notes = engineer_features(clean_df, team_df, pm_df, lineup_df, pbp_df)
    train_df, test_df = split_train_test_by_match(featured, 0.8)
    model, mae, num_cols, cat_cols = train_model(train_df, test_df)

    pred_cols=num_cols+cat_cols
    if not test_df.empty:
        raw_preds=np.asarray(model.predict(test_df[pred_cols]), dtype=float)
        preds=refine_cold_start_predictions(test_df, raw_preds)
        refined_mask = np.abs(preds - raw_preds) > 1e-12
        if refined_mask.any():
            logger.info(
                'Cold-start refinement adjusted %d/%d holdout predictions (avg |delta|=%.3f)',
                int(refined_mask.sum()),
                int(len(preds)),
                float(np.mean(np.abs(preds[refined_mask] - raw_preds[refined_mask]))),
            )
        test_predictions=test_df[['Match_ID','Match_Date','Team','Opponent','Name','No.','EF']].copy()
        test_predictions=test_predictions.rename(columns={'EF':'actual_EF'})
        test_predictions['predicted_EF']=preds
        test_predictions['abs_error']=(test_predictions['actual_EF']-test_predictions['predicted_EF']).abs()
        mae = float(mean_absolute_error(test_predictions['actual_EF'], test_predictions['predicted_EF']))
    else:
        test_predictions=pd.DataFrame(columns=['Match_ID','Match_Date','Team','Opponent','Name','No.','actual_EF','predicted_EF','abs_error'])

    export_csv(featured,output_csv)
    export_csv(log_df,output_log_csv)
    export_csv(test_predictions,output_pred_csv)

    state = MasterState(
        player_data=clean_df.copy(),
        team_profiles=team_df.copy(),
        model_pipeline=model,
        pm_data=pm_df.copy() if not pm_df.empty else None,
        lineup_data=lineup_df.copy() if not lineup_df.empty else None,
        pbp_data=pbp_df.copy() if not pbp_df.empty else None,
    )

    # Compute predicted spread stats for regression-to-the-mean diagnostics
    _pred_std = float(test_predictions['predicted_EF'].std()) if not test_predictions.empty else None
    _actual_std = float(test_predictions['actual_EF'].std()) if not test_predictions.empty else None

    summary={
      'mae':mae,
      'predicted_ef_stddev': _pred_std,
      'actual_ef_stddev': _actual_std,
      'num_pdf_files_total':len(discover_pdf_files(data_dir)),
      'num_box_score_pdfs_processed':len([p for p in discover_pdf_files(data_dir) if 'box score' in p.name.lower()]),
      'num_plusminus_pdfs_processed': len([p for p in discover_pdf_files(data_dir) if 'plusminus' in p.name.lower()]),
      'num_lineup_pdfs_processed': len([p for p in discover_pdf_files(data_dir) if 'line up analysis' in p.name.lower()]),
      'num_pbp_pdfs_processed': len([p for p in discover_pdf_files(data_dir) if 'play by play' in p.name.lower()]),
      'num_matches_processed':int(featured['Match_ID'].nunique()) if not featured.empty else 0,
      'num_player_rows_extracted':int(len(raw_df)),
      'num_usable_rows_after_cleaning':int(len(clean_df)),
      'pm_rows_extracted': int(len(pm_df)),
      'lineup_rows_extracted': int(len(lineup_df)),
      'pbp_rows_extracted': int(len(pbp_df)),
      'final_feature_columns_numeric':num_cols,
      'final_feature_columns_categorical':cat_cols,
      'skipped_or_failed_logs':log_df[log_df['status'].isin(['skipped','failed','partially_extracted'])].to_dict(orient='records') if not log_df.empty else [],
      'cleaning_summary':clean_sum,
      'assumptions_and_fallbacks':notes,
      'output_csv':str(output_csv),
      'processing_log_csv':str(output_log_csv),
      'test_predictions_csv':str(output_pred_csv),
      'test_prediction_rows':int(len(test_predictions))
    }
    return summary, state


def run_pipeline(data_dir: Path, output_csv: Path, output_log_csv: Path, output_pred_csv: Path) -> dict[str, Any]:
    """Backward-compatible wrapper: bootstrap then snapshot into the module globals."""
    summary, state = bootstrap_state(data_dir, output_csv, output_log_csv, output_pred_csv)
    _assign_globals(state)
    return summary
