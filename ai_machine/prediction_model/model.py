from __future__ import annotations

from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd
from sklearn.compose import ColumnTransformer
from sklearn.ensemble import RandomForestRegressor
from sklearn.impute import SimpleImputer
from sklearn.metrics import mean_absolute_error
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import OneHotEncoder

from config import (
    COLD_START_DEFAULT,
    COLD_START_LEAGUE,
    COLD_START_PLAYER,
    COLD_START_SIMILARITY,
    COLD_START_TEAM,
    DEFAULT_FILL,
    HIST_STATS,
    SUPP_NUMERIC_COLS,
    logger,
)

def train_model(train_df: pd.DataFrame, test_df: pd.DataFrame) -> tuple[Pipeline, float | None, list[str], list[str]]:
    num = [
      # --- Box-score career averages (7) — core signal ---
      f'Overall_Avg_{s}' for s in HIST_STATS
    ] + [
      # --- Appearance/activity (2) ---
      'Past_Appearances', 'Player_First_Appearance',
      # --- Recency: last game (3) — non-redundant subset ---
      'Last_EF', 'Last_PTS', 'Last_MIN_num',
      # --- Extreme spread (1) — only EF_Max_Prior (= Last_EF for 1-game; diverges with more data) ---
      'EF_Max_Prior',
      # --- Opponent profile (3) — 1 indicator + 2 representative of 6 correlated opp features ---
      'Opponent_Profile_ColdStart',
      'opp_profile_def_rebounds', 'opp_profile_turnovers',
      # --- Supplementary: strongest from each source (4) ---
      'hist_pm_avg_on_diff', 'hist_pm_avg_net_impact',
      'hist_lineup_avg_weighted_diff',
      'hist_pbp_avg_turnovers',
      # --- Cosine similarity (3) — keep full set; zero in train, active in test ---
      'Top1_Similarity', 'Top3_Neighbor_EF_Avg', 'Top3_Neighbor_MIN_Avg',
      # --- Cold-start level (1) ---
      'Cold_Start_Level',
    ]
    cat = ['Team_Code', 'Opponent_Code']

    # -----------------------------------------------------------------
    # Model comparison framework — temporal evaluation only.
    # Train on matches 0..N-1, test on match N (already done by caller).
    # -----------------------------------------------------------------
    y_train = train_df['EF'].astype(float)
    y_test = test_df['EF'].astype(float) if not test_df.empty else pd.Series(dtype=float)

    prep_tree = ColumnTransformer([
        ('num', SimpleImputer(strategy='constant', fill_value=DEFAULT_FILL), num),
        ('cat', OneHotEncoder(handle_unknown='ignore'), cat)
    ], remainder='drop')

    final_pipe = Pipeline([
        ('preprocess', prep_tree),
        ('model', RandomForestRegressor(
            n_estimators=300,
            max_depth=4,
            min_samples_leaf=3,
            max_features=0.6,
            random_state=42,
            n_jobs=-1,
        )),
    ])
    final_pipe.fit(train_df[num + cat], y_train)

    mae = None
    if not test_df.empty:
        preds = final_pipe.predict(test_df[num + cat])
        mae = float(mean_absolute_error(y_test, preds))
        pred_std = float(np.std(preds))
        actual_std = float(np.std(y_test))
        std_ratio = pred_std / actual_std if actual_std > 0 else 0.0
        logger.info('--- Model Selection (Phase 3 RF Refinement) ---')
        logger.info('Forced production model: RF_constrained')
        logger.info('Holdout MAE=%.3f, PredStd=%.3f, StdRatio=%.3f', mae, pred_std, std_ratio)
    else:
        logger.info('--- Model Selection (Phase 3 RF Refinement) ---')
        logger.info('Forced production model: RF_constrained (no holdout rows available)')

    logger.info('Curated feature count: %d numeric + %d categorical', len(num), len(cat))
    return final_pipe, mae, num, cat  # Phase 3 stops here; legacy model-selection code below is bypassed.


def _safe_float_or_zero(value: Any) -> float:
    if pd.isna(value):
        return 0.0
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def _get_similarity_prior(row: pd.Series) -> float | None:
    top3 = _safe_float_or_zero(row.get('Top3_Neighbor_EF_Avg', 0.0))
    top5 = _safe_float_or_zero(row.get('Top5_Neighbor_EF_Avg', 0.0))

    if top3 > 0.0 and top5 > 0.0:
        return 0.75 * top3 + 0.25 * top5
    if top3 > 0.0:
        return top3
    if top5 > 0.0:
        return top5
    return None


def _get_cold_start_similarity_weight(row: pd.Series) -> float:
    sim_strength = max(
        _safe_float_or_zero(row.get('Top1_Similarity', 0.0)),
        _safe_float_or_zero(row.get('Top3_Similarity_Avg', 0.0)),
        _safe_float_or_zero(row.get('Top5_Similarity_Avg', 0.0)),
    )
    if sim_strength < 0.80:
        return 0.0

    past_appearances = int(_safe_float_or_zero(row.get('Past_Appearances', 0.0)))
    cold_start_level = int(_safe_float_or_zero(row.get('Cold_Start_Level', COLD_START_DEFAULT)))
    base_weight = 0.0

    if past_appearances == 0:
        if cold_start_level == COLD_START_SIMILARITY:
            base_weight = 0.45
        elif cold_start_level == COLD_START_TEAM:
            base_weight = 0.35
        elif cold_start_level == COLD_START_LEAGUE:
            base_weight = 0.30
        elif cold_start_level == COLD_START_DEFAULT:
            base_weight = 0.25
    # One prior appearance is still weak-history, so allow a small blend.
    elif past_appearances == 1:
        base_weight = 0.20

    if base_weight == 0.0:
        return 0.0

    confidence = min(1.0, max(0.0, (sim_strength - 0.80) / 0.20))
    return base_weight * confidence


def _build_similarity_refinement_context(test_df: pd.DataFrame) -> tuple[
    dict[tuple[str, str, int], dict[str, float]], dict[float, int]
]:
    aligned_test = test_df.reset_index(drop=True).copy()
    aligned_test['sim_prior'] = aligned_test.apply(_get_similarity_prior, axis=1)
    aligned_test['weak_history_flag'] = (
        aligned_test['Past_Appearances'].fillna(0).le(1)
        | aligned_test['Player_First_Appearance'].fillna(0).eq(1)
        | aligned_test['Cold_Start_Level'].fillna(0).gt(0)
    )
    weak_df = aligned_test[
        aligned_test['weak_history_flag'] & aligned_test['sim_prior'].notna()
    ].copy()
    if weak_df.empty:
        return {}, {}

    weak_df['group_key'] = list(zip(
        weak_df['Team'].astype(str),
        weak_df['Opponent'].astype(str),
        weak_df['Cold_Start_Level'].fillna(COLD_START_DEFAULT).astype(int),
    ))
    weak_df['sim_prior_rounded'] = weak_df['sim_prior'].round(6)

    # --- Collect neighbor-EF values for CV computation ---
    weak_df['_top3_nef'] = pd.to_numeric(
        weak_df.get('Top3_Neighbor_EF_Avg'), errors='coerce'
    ).fillna(0.0)
    weak_df['_top5_nef'] = pd.to_numeric(
        weak_df.get('Top5_Neighbor_EF_Avg'), errors='coerce'
    ).fillna(0.0)

    group_stats: dict[tuple[str, str, int], dict[str, float]] = {}
    for group_key, group in weak_df.groupby('group_key', sort=False):
        sim_vals = pd.to_numeric(group['sim_prior'], errors='coerce').dropna()
        sim_range = float(sim_vals.max() - sim_vals.min()) if not sim_vals.empty else 0.0

        # Coefficient of variation of neighbor-EF across group members.
        # This measures whether the similarity-based EF estimate actually
        # differs between players in the same contextual group.
        nef_vals = group['sim_prior'].values.astype(float)
        nef_mean = float(np.mean(nef_vals)) if len(nef_vals) > 0 else 0.0
        nef_std = float(np.std(nef_vals)) if len(nef_vals) > 1 else 0.0
        nef_cv = (nef_std / abs(nef_mean)) if abs(nef_mean) > 1e-9 else 0.0

        # Also compute CV directly on the neighbor EF averages (Top3/Top5)
        # which are the actual values blended into the prediction.
        t3_vals = group['_top3_nef'].values.astype(float)
        t5_vals = group['_top5_nef'].values.astype(float)
        # Use the average of both neighbor-EF columns per player as the
        # discriminative target (same weighting as _get_similarity_prior).
        blended_nef = 0.75 * t3_vals + 0.25 * t5_vals
        blended_mean = float(np.mean(blended_nef)) if len(blended_nef) > 0 else 0.0
        blended_std = float(np.std(blended_nef)) if len(blended_nef) > 1 else 0.0
        blended_cv = (
            blended_std / abs(blended_mean)
        ) if abs(blended_mean) > 1e-9 else 0.0

        group_stats[group_key] = {
            'rows': float(len(group)),
            'sim_prior_nunique': float(
                group['sim_prior_rounded'].nunique(dropna=True)
            ),
            'sim_prior_range': sim_range,
            'neighbor_ef_cv': float(blended_cv),
            'neighbor_ef_std': float(blended_std),
        }

    prior_counts = {
        float(k): int(v)
        for k, v in weak_df['sim_prior_rounded']
        .value_counts(dropna=True)
        .to_dict()
        .items()
    }
    return group_stats, prior_counts


def _get_similarity_discrimination_factor(
    row: pd.Series,
    sim_prior: float,
    group_stats: dict[tuple[str, str, int], dict[str, float]],
    prior_counts: dict[float, int],
) -> float:
    """Compute a [0, 1] scaling factor for the similarity blend weight.

    The factor is 1.0 when the similarity prior provides genuinely
    player-specific information, and shrinks toward 0.0 when the prior
    is shared across many cold-start players in the same contextual
    group — meaning it cannot discriminate between them.

    Decision criteria (applied sequentially, first match wins):

    1. **Neighbor-EF coefficient of variation (CV)**:
       - CV < 1%  → effectively identical neighbor-EF across group → 0.0
       - CV < 5%  → very low discrimination → 0.10
       - CV < 10% → moderate discrimination → 0.35

    2. **Dominance ratio** (fraction of group sharing the exact same
       rounded prior):
       - > 80% of the group shares the same prior value → 0.15

    3. **Prior-range check** (absolute spread of prior values in the
       group):
       - range < 0.5 EF units AND group size ≥ 2 → 0.30

    4. Otherwise → 1.0 (full weight; similarity adds real signal).
    """
    group_key = (
        str(row.get('Team', '')),
        str(row.get('Opponent', '')),
        int(_safe_float_or_zero(row.get('Cold_Start_Level', COLD_START_DEFAULT))),
    )
    stats = group_stats.get(group_key)
    if not stats:
        # Player is the only weak-history member in this context — the
        # similarity prior is unique by definition.
        return 1.0

    group_rows = int(stats['rows'])
    if group_rows <= 1:
        # Singleton group — no collapse risk.
        return 1.0

    neighbor_ef_cv = float(stats['neighbor_ef_cv'])
    neighbor_ef_std = float(stats['neighbor_ef_std'])

    # -----------------------------------------------------------------
    # Criterion 1: Coefficient of variation of neighbor-EF values.
    # This is the most reliable indicator because it measures whether
    # the blended output (the value actually mixed into predictions)
    # varies across group members.  Scale-invariant.
    # -----------------------------------------------------------------
    if neighbor_ef_cv < 0.01:
        # Essentially identical neighbor-EF for all group members.
        # The similarity prior cannot separate them — suppress fully.
        return 0.0
    if neighbor_ef_cv < 0.05:
        # Very low discrimination.  Allow a tiny residual blend so the
        # model is not completely blind to neighbor signal, but the RF
        # prediction dominates.
        return 0.10
    if neighbor_ef_cv < 0.10:
        # Moderate discrimination — shrink but preserve.
        return 0.35

    # -----------------------------------------------------------------
    # Criterion 2: Dominance ratio — how many rows share the exact
    # same rounded prior value as this player.
    # Even if group-wide CV is acceptable, if *this* player's prior
    # is shared by the vast majority, it is effectively a group
    # constant for that subset.
    # -----------------------------------------------------------------
    prior_key = round(float(sim_prior), 6)
    prior_count = prior_counts.get(prior_key, 0)
    dominance_ratio = prior_count / group_rows if group_rows > 0 else 0.0
    if dominance_ratio > 0.80 and prior_count >= 2:
        return 0.15

    # -----------------------------------------------------------------
    # Criterion 3: Absolute range of similarity priors in the group.
    # If the spread is less than 0.5 EF units across ≥ 2 players,
    # the prior adds little unique signal even if CV passes.
    # -----------------------------------------------------------------
    sim_range = float(stats['sim_prior_range'])
    if group_rows >= 2 and sim_range < 0.5 and neighbor_ef_std < 0.5:
        return 0.30

    # -----------------------------------------------------------------
    # Default: similarity prior appears genuinely discriminative.
    # -----------------------------------------------------------------
    return 1.0


def refine_cold_start_predictions(test_df: pd.DataFrame, raw_preds: np.ndarray) -> np.ndarray:
    """Refine RF predictions for cold-start players using similarity priors.

    Apply similarity-based blending ONLY when the similarity prior provides
    genuinely player-specific discriminative information.  If neighbor-EF
    values are nearly identical across a contextual group, the similarity
    prior is suppressed and the RF prediction is trusted instead.
    """
    refined_preds = np.asarray(raw_preds, dtype=float).copy()
    if test_df.empty or refined_preds.size == 0:
        return refined_preds

    aligned_test = test_df.reset_index(drop=True)
    group_stats, prior_counts = _build_similarity_refinement_context(aligned_test)

    # --- Diagnostic counters ---
    _n_eligible = 0       # rows with a valid sim_prior and nonzero base weight
    _n_suppressed = 0     # discrimination_factor == 0  → RF unchanged
    _n_partial = 0        # 0 < discrimination_factor < 1  → reduced blend
    _n_full = 0           # discrimination_factor == 1  → full blend
    _factor_bins: dict[str, int] = defaultdict(int)  # factor → count

    for idx, row in aligned_test.iterrows():
        sim_prior = _get_similarity_prior(row)
        if sim_prior is None:
            continue

        sim_weight = _get_cold_start_similarity_weight(row)
        if sim_weight <= 0.0:
            continue

        _n_eligible += 1

        discrimination_factor = _get_similarity_discrimination_factor(
            row=row,
            sim_prior=sim_prior,
            group_stats=group_stats,
            prior_counts=prior_counts,
        )

        _factor_bins[f'{discrimination_factor:.2f}'] += 1

        if discrimination_factor <= 0.0:
            _n_suppressed += 1
            continue
        elif discrimination_factor < 1.0:
            _n_partial += 1
        else:
            _n_full += 1

        sim_weight *= discrimination_factor

        refined_preds[idx] = ((1.0 - sim_weight) * refined_preds[idx]) + (sim_weight * sim_prior)

    # --- Log discrimination diagnostics ---
    if _n_eligible > 0:
        logger.info(
            'Cold-start discrimination: %d eligible, %d suppressed (factor=0), '
            '%d partial (0<f<1), %d full (f=1). Factor distribution: %s',
            _n_eligible, _n_suppressed, _n_partial, _n_full,
            dict(_factor_bins),
        )
    # --- Log group-level CV details ---
    for gk, gs in group_stats.items():
        if gs['rows'] >= 2:
            logger.info(
                '  Group %s: rows=%d, neighbor_ef_cv=%.4f, neighbor_ef_std=%.4f, '
                'sim_range=%.4f, nunique=%d',
                gk, int(gs['rows']), gs['neighbor_ef_cv'], gs['neighbor_ef_std'],
                gs['sim_prior_range'], int(gs['sim_prior_nunique']),
            )

    return refined_preds


def export_csv(df: pd.DataFrame, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    out=df.copy()

    # Export dates as text to avoid Excel #### display for narrow date columns.
    if 'Match_Date' in out.columns:
        _md=pd.to_datetime(out['Match_Date'], errors='coerce').dt.strftime('%Y-%m-%d').fillna('')
        out['Match_Date']=_md.map(lambda x: f'="{x}"' if x else '')

    # Deterministic fill for missing supplemental report features.
    for c in SUPP_NUMERIC_COLS:
        if c in out.columns:
            out[c]=pd.to_numeric(out[c], errors='coerce').fillna(0.0)

    if 'Opponent_Profile_Proxy_Team' in out.columns:
        out['Opponent_Profile_Proxy_Team']=out['Opponent_Profile_Proxy_Team'].fillna('NO_PROXY')

    out.to_csv(path,index=False)
