from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Any

import pandas as pd
from sklearn.pipeline import Pipeline

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("fiba_pipeline")

HIST_STATS = ["PTS", "REB", "AST", "STL", "BLK", "TO", "MIN_num"]

OPP_FEATURES = [
    "opp_profile_fast_break_points",
    "opp_profile_def_rebounds",
    "opp_profile_turnovers",
    "opp_profile_points_from_turnovers",
    "opp_profile_second_chance_points",
    "opp_profile_bench_points",
]

DEFAULT_FILL = 0.0

HIST_PM_FEATURES = [
    "hist_pm_avg_on_diff",
    "hist_pm_avg_off_diff",
    "hist_pm_avg_net_impact",
    "hist_pm_avg_on_pts_per_min",
    "hist_pm_trend_on_diff",
    "hist_pm_stddev_on_diff",
]

HIST_LINEUP_FEATURES = [
    "hist_lineup_avg_segments",
    "hist_lineup_avg_weighted_diff",
    "hist_lineup_avg_best_diff",
    "hist_lineup_avg_worst_diff",
    "hist_lineup_avg_total_minutes",
    "hist_lineup_stability",
]

HIST_PBP_FEATURES = [
    "hist_pbp_avg_substitutions",
    "hist_pbp_avg_turnovers",
    "hist_pbp_avg_fouls",
    "hist_pbp_avg_off_reb",
    "hist_pbp_avg_def_reb",
    "hist_pbp_avg_steals",
    "hist_pbp_avg_assists",
    "hist_pbp_avg_fast_break",
    "hist_pbp_trend_turnovers",
    "hist_pbp_2pt_rate",
    "hist_pbp_3pt_rate",
    "hist_pbp_ft_rate",
]

EXTREME_FEATURES = [
    "EF_Max_Prior",
    "EF_Min_Prior",
    "EF_Range_Prior",
    "EF_Pct_Above_Mean",
    "Last_2_EF_Avg",
    "EF_Ewm_2",
]

SIMILARITY_FEATURES = [
    "Top1_Similarity",
    "Top3_Similarity_Avg",
    "Top5_Similarity_Avg",
    "Top3_Neighbor_EF_Avg",
    "Top5_Neighbor_EF_Avg",
    "Top3_Neighbor_PTS_Avg",
    "Top3_Neighbor_MIN_Avg",
]

SIMILARITY_PROFILE_DIMS = [f"Overall_Avg_{stat}" for stat in HIST_STATS]

COLD_START_PLAYER = 0
COLD_START_SIMILARITY = 1
COLD_START_TEAM = 2
COLD_START_LEAGUE = 3
COLD_START_DEFAULT = 4

SUPP_NUMERIC_COLS = [
    "pm_on_minutes", "pm_off_minutes", "pm_on_diff", "pm_off_diff",
    "pm_on_points_per_min", "pm_off_points_per_min", "pm_on_assists", "pm_off_assists",
    "pm_on_rebounds", "pm_off_rebounds", "pm_on_steals", "pm_off_steals",
    "pm_on_turnovers", "pm_off_turnovers",
    "lineup_segments", "lineup_avg_diff", "lineup_best_diff", "lineup_worst_diff",
    "lineup_avg_pts_per_min", "lineup_total_minutes",
    "rotation_segments", "rotation_avg_diff", "rotation_best_diff", "rotation_worst_diff",
    "rotation_avg_reb", "rotation_avg_ast", "rotation_total_minutes",
    "shot_fg_made", "shot_fg_att", "shot_fg_pct_report",
    "shot_2pt_made", "shot_2pt_att", "shot_2pt_pct_report",
    "shot_3pt_made", "shot_3pt_att", "shot_3pt_pct_report",
    "shot_ft_made", "shot_ft_att", "shot_ft_pct_report",
    "pbp_event_lines", "pbp_turnover_events", "pbp_substitutions",
    "pbp_foul_events", "pbp_off_reb_events", "pbp_def_reb_events",
    "pbp_2pt_events", "pbp_3pt_events", "pbp_ft_events",
]


@dataclass
class ExtractionResult:
    player_rows: list[dict[str, Any]]
    team_profiles: list[dict[str, Any]]
    logs: list[dict[str, Any]]
