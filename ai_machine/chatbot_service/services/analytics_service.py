from __future__ import annotations

import re
from functools import lru_cache
from pathlib import Path
from typing import Any

import pandas as pd

from analytics import (
    COUNTING_COLUMNS,
    enrich_box_scores,
    load_box_scores,
    normalize_text,
)


def _mtime(path: Path) -> float:
    return path.stat().st_mtime if path.exists() else 0.0


@lru_cache(maxsize=64)
def _cached_box_scores(csv_path_text: str, mtime: float) -> pd.DataFrame:
    del mtime
    return load_box_scores(Path(csv_path_text))


def load_project_box_scores(csv_path: Path) -> pd.DataFrame:
    return _cached_box_scores(str(csv_path), _mtime(csv_path))


# ---------------------------------------------------------------------------
# DB → box-score adapter (Phase 0 data unification).
#
# The analytics/prediction stack was built to read a PDF-derived box-score CSV.
# When no PDF has been ingested for a team (or the CSV is missing the team the
# coach asked about) the live DB still holds the real per-player numbers via
# AppDataClient.get_match_player_stats. This adapter reshapes those rows into the
# exact DataFrame shape `analytics` expects (COUNTING_COLUMNS + player_name/team/
# minutes/date), so recommend_squad / rank_players / answer_stat_query work off the DB
# without a PDF. Mirrors load_box_scores: coerce counting columns, then enrich.
# ---------------------------------------------------------------------------

# granularity values that are season/aggregate rollups, not single games. Keeping
# them would double-count, so the adapter drops them and uses per-game rows only.
_CUMULATIVE_GRAINS = {
    "cumulative", "total", "totals", "overall", "season", "aggregate",
    "sum", "summary", "all",
}


def _split_made_attempted(value: Any) -> tuple[int, int]:
    """'20/44' -> (20, 44); tolerant of spaces/floats; junk/empty -> (0, 0)."""
    match = re.match(r"\s*(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)\s*$", str(value or ""))
    if not match:
        return 0, 0
    return int(float(match.group(1))), int(float(match.group(2)))


def _minutes_mmss(value: Any) -> str:
    """Normalise a minutes value to the 'MM:SS' string analytics.minutes_to_seconds parses."""
    if value is None:
        return "0:00"
    text = str(value).strip()
    if ":" in text:
        return text
    try:
        total = float(text)
    except ValueError:
        return "0:00"
    minutes = int(total)
    seconds = int(round((total - minutes) * 60))
    if seconds >= 60:  # guard rounding to :60
        minutes += 1
        seconds = 0
    return f"{minutes}:{seconds:02d}"


def _box_score_date(value: Any) -> str:
    """Normalize API/DB dates to the PDF CSV style: '28 Feb 2026'."""
    if value is None:
        return ""
    text = str(value).strip()
    if not text:
        return ""
    is_iso_like = bool(re.match(r"^\d{4}-\d{1,2}-\d{1,2}(?:[T\s]|$)", text))
    parsed = pd.to_datetime(text, errors="coerce", dayfirst=not is_iso_like)
    if pd.isna(parsed):
        return text
    return parsed.strftime("%d %b %Y")


def box_scores_from_match_player_stats(
    rows: list[dict[str, Any]] | None,
    team: str = "EGY",
) -> pd.DataFrame:
    """
    Reshape AppDataClient.get_match_player_stats rows into the analytics box-score
    DataFrame. Returns an empty DataFrame when there are no usable per-game rows.
    `team` is the abbreviation written into the team column so team-scoped queries
    (which filter via analytics.normalise_team) still match.
    """
    if not rows:
        return pd.DataFrame()

    records: list[dict[str, Any]] = []
    for row in rows:
        if not isinstance(row, dict):
            continue
        grain = str(row.get("granularity") or "").strip().lower()
        if grain in _CUMULATIVE_GRAINS:
            continue
        name = str(row.get("name") or row.get("player_name") or "").strip()
        if not name:
            continue

        two_made, two_attempted = _split_made_attempted(row.get("two_pt_ma"))
        three_made, three_attempted = _split_made_attempted(row.get("three_pt_ma"))
        ft_made, ft_attempted = _split_made_attempted(row.get("ft_ma"))
        opponent = str(row.get("opponent_name") or row.get("matchup") or "").strip()
        date = _box_score_date(
            row.get("date")
            or row.get("match_date")
            or row.get("event_start_at")
            or row.get("event_start")
            or row.get("created_at")
        )

        records.append(
            {
                "player_name": name,
                "team": team,
                "match_name": f"vs {opponent}" if opponent else "",
                "date": date,
                "opponent": opponent,
                "minutes": _minutes_mmss(row.get("minutes")),
                "two_made": two_made,
                "two_attempted": two_attempted,
                "three_made": three_made,
                "three_attempted": three_attempted,
                "ft_made": ft_made,
                "ft_attempted": ft_attempted,
                "fg_made": two_made + three_made,
                "fg_attempted": two_attempted + three_attempted,
                "offensive_rebounds": row.get("offensive_rebounds") or 0,
                "defensive_rebounds": row.get("defensive_rebounds") or 0,
                "total_rebounds": row.get("total_rebounds") or 0,
                "assists": row.get("assists") or 0,
                "turnovers": row.get("turnovers") or 0,
                "steals": row.get("steals") or 0,
                "blocks": row.get("blocks") or 0,
                "personal_fouls": row.get("personal_fouls") or 0,
                "fouls_drawn": row.get("fouls_drawn") or 0,
                "plus_minus": row.get("plus_minus") or 0,
                "efficiency": row.get("efficiency") or 0,
                "points": row.get("points") or 0,
            }
        )

    if not records:
        return pd.DataFrame()

    df = pd.DataFrame.from_records(records)
    for column in COUNTING_COLUMNS:
        if column in df.columns:
            df[column] = pd.to_numeric(df[column], errors="coerce").fillna(0)
    return enrich_box_scores(df)


# ---------------------------------------------------------------------------
# Fallback-only union (Phase 1).
#
# The AUTHORITATIVE path is the .NET unified-box-scores view, which deduplicates
# the PDF-imported vs coach-entered overlap in SQL — so on the default path the
# chatbot does NO dedup. This helper exists only for deployments where that
# endpoint isn't live yet and the operator opts into BOX_SCORE_SOURCE=union: it
# concatenates the PDF-derived CSV frame and the DB-derived frame and collapses
# rows that describe the same (player, game) so a game present in both planes is
# not double-counted. The dedup key is best-effort (player + opponent/matchup),
# which is why this is a fallback, not the default.
# ---------------------------------------------------------------------------

def _row_matchup_token(row: "pd.Series") -> str:
    """A normalized opponent/game token shared by both frames for dedup.

    Prefers an explicit opponent column; falls back to match_name (e.g. 'vs Angola'),
    stripping a leading 'vs'. Returns '' when nothing identifies the game. NaN/blank
    values are skipped so a missing opponent never collapses unrelated rows."""
    for col in ("opponent", "opponent_name", "match_name", "matchup"):
        value = row.get(col) if hasattr(row, "get") else None
        if value is None or value == "" or (isinstance(value, float) and pd.isna(value)):
            continue
        text = normalize_text(str(value))
        if text and text != "nan":
            return re.sub(r"^vs\s+", "", text).strip()
    return ""


def box_scores_union(
    csv_df: pd.DataFrame | None,
    db_df: pd.DataFrame | None,
    *,
    source_priority: str = "db",
) -> pd.DataFrame:
    """Concatenate the CSV and DB box-score frames, dropping same-game duplicates.

    A row present in both planes (same normalized player + opponent/game token)
    collapses to one. `source_priority` ('db' or 'csv') decides which plane's values
    survive a collision. Frames with differing columns are aligned by pandas (missing
    columns become NaN). Empty/None inputs degrade gracefully to the other frame.
    """
    csv_ok = isinstance(csv_df, pd.DataFrame) and not csv_df.empty
    db_ok = isinstance(db_df, pd.DataFrame) and not db_df.empty
    if not csv_ok and not db_ok:
        return pd.DataFrame()
    if not csv_ok:
        return db_df.reset_index(drop=True)
    if not db_ok:
        return csv_df.reset_index(drop=True)

    csv = csv_df.copy()
    db = db_df.copy()
    # Lower priority value sorts first and is kept by drop_duplicates(keep="first").
    csv["_src_rank"] = 0 if source_priority == "csv" else 1
    db["_src_rank"] = 0 if source_priority == "db" else 1

    combined = pd.concat([csv, db], ignore_index=True, sort=False)
    player_key = combined.get("player_name")
    if player_key is None:
        return combined.drop(columns=["_src_rank"], errors="ignore").reset_index(drop=True)
    combined["_player_key"] = player_key.astype(str).map(normalize_text)
    combined["_game_key"] = combined.apply(_row_matchup_token, axis=1)
    combined = (
        combined.sort_values("_src_rank", kind="stable")
        .drop_duplicates(subset=["_player_key", "_game_key"], keep="first")
    )
    return combined.drop(
        columns=["_src_rank", "_player_key", "_game_key"], errors="ignore"
    ).reset_index(drop=True)
