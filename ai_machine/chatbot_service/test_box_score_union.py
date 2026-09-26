"""
Phase 1 box-score union / unified-consumer tests.

Phase 1 moves the PDF-vs-DB dedup OUT of Python: the authoritative path is the .NET
``unified-box-scores`` view, which the chatbot consumes verbatim (no Python dedup).
The pandas ``box_scores_union`` survives only as an offline fallback for deployments
where that endpoint isn't live yet. These tests pin down both:

  * the adapter consumes a unified-style payload unchanged — already deduplicated
    upstream, so counts are correct without ANY Python merge step; and
  * the fallback ``box_scores_union`` collapses a game present in both planes to one
    row (no double-count), keeps genuinely distinct games, honours source priority on
    a collision, and degrades gracefully on empty inputs.

Run directly (``python test_box_score_union.py``) or via pytest.
"""
from __future__ import annotations

import os
import sys

# Make the chatbot package root importable regardless of the caller's CWD.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from analytics import rank_players, rank_weighted_score  # noqa: E402
from services.analytics_service import (  # noqa: E402
    box_scores_from_match_player_stats,
    box_scores_union,
)


def _row(
    name: str,
    opponent: str,
    points: int,
    granularity: str = "game_player",
    date: str | None = None,
    blocks: int = 0,
    defensive_rebounds: int = 4,
) -> dict:
    """A minimal unified/match-player-stats row for the adapter."""
    row = {
        "name": name,
        "opponent_name": opponent,
        "granularity": granularity,
        "game_no": "1",
        "minutes": "20:00",
        "two_pt_ma": "4/8",
        "three_pt_ma": "1/3",
        "ft_ma": "2/2",
        "total_rebounds": 5,
        "defensive_rebounds": defensive_rebounds,
        "assists": 3,
        "blocks": blocks,
        "points": points,
    }
    if date is not None:
        row["date"] = date
    return row


# ---------------------------------------------------------------------------
# Unified payload is consumed unchanged (the default Phase 1 path).
# ---------------------------------------------------------------------------
def test_unified_payload_consumed_without_python_dedup() -> None:
    # A unified-view payload: already per-game-only, already deduplicated upstream.
    payload = [
        _row("Ahmed Hassan", "Angola", 22),
        _row("Omar Khaled", "Angola", 9),
    ]
    df = box_scores_from_match_player_stats(payload, team="EGY")
    # Two players, one game each — no row was dropped or duplicated by the adapter.
    assert len(df) == 2
    assert set(df["player_name"]) == {"Ahmed Hassan", "Omar Khaled"}
    assert int(df[df["player_name"] == "Ahmed Hassan"].iloc[0]["points"]) == 22


# ---------------------------------------------------------------------------
# Fallback union: same game in both planes collapses to one row.
# ---------------------------------------------------------------------------
def test_union_dedups_same_game_no_double_count() -> None:
    csv_df = box_scores_from_match_player_stats([_row("Ahmed Hassan", "Angola", 20)], team="EGY")
    db_df = box_scores_from_match_player_stats([_row("Ahmed Hassan", "Angola", 22)], team="EGY")

    merged = box_scores_union(csv_df, db_df, source_priority="db")
    # One physical game, present in both planes -> exactly one row (not summed to 42).
    assert len(merged) == 1
    # db priority wins the collision.
    assert int(merged.iloc[0]["points"]) == 22

    merged_csv = box_scores_union(csv_df, db_df, source_priority="csv")
    assert len(merged_csv) == 1
    assert int(merged_csv.iloc[0]["points"]) == 20


def test_union_keeps_distinct_games() -> None:
    csv_df = box_scores_from_match_player_stats([_row("Ahmed Hassan", "Angola", 20)], team="EGY")
    db_df = box_scores_from_match_player_stats([_row("Ahmed Hassan", "Mali", 18)], team="EGY")
    merged = box_scores_union(csv_df, db_df, source_priority="db")
    # Different opponents -> two different games -> both retained.
    assert len(merged) == 2
    assert set(merged["points"].astype(int)) == {20, 18}


def test_union_matchup_token_equivalence_via_match_name() -> None:
    # One plane only carries match_name ('vs Angola'), the other carries opponent.
    csv_df = box_scores_from_match_player_stats([_row("Ahmed Hassan", "Angola", 20)], team="EGY")
    csv_df = csv_df.drop(columns=["opponent"])  # force the match_name fallback in the key
    db_df = box_scores_from_match_player_stats([_row("Ahmed Hassan", "Angola", 22)], team="EGY")
    merged = box_scores_union(csv_df, db_df, source_priority="db")
    # 'vs Angola' (match_name) and 'Angola' (opponent) must resolve to the same game.
    assert len(merged) == 1


def test_union_empty_inputs() -> None:
    db_df = box_scores_from_match_player_stats([_row("Ahmed Hassan", "Angola", 22)], team="EGY")
    import pandas as pd

    assert box_scores_union(None, None).empty
    assert box_scores_union(pd.DataFrame(), pd.DataFrame()).empty
    # One side empty -> the other passes through unchanged.
    assert len(box_scores_union(pd.DataFrame(), db_df)) == 1
    assert len(box_scores_union(db_df, pd.DataFrame())) == 1


def test_adapter_normalizes_api_date_for_analytics() -> None:
    df = box_scores_from_match_player_stats(
        [_row("Ahmed Hassan", "Angola", 22, date="2026-03-01T18:30:00Z")],
        team="EGY",
    )
    assert df.iloc[0]["date"] == "01 Mar 2026"


def test_weighted_score_handles_unified_frame_without_date_column() -> None:
    df = box_scores_from_match_player_stats(
        [
            _row("Ahmed Hassan", "Angola", 22, blocks=2, defensive_rebounds=7),
            _row("Omar Khaled", "Mali", 9, blocks=1, defensive_rebounds=3),
        ],
        team="EGY",
    ).drop(columns=["date"])

    result = rank_weighted_score(
        df,
        metrics=["blocks", "defensive_rebounds"],
        weights={"blocks": 2, "defensive_rebounds": 1},
        team="EGY",
        top_n=2,
    )

    assert "weighted score" in result["answer"]
    assert "Ahmed Hassan" in result["answer"]


def test_rank_metric_handles_unified_frame_without_date_column() -> None:
    df = box_scores_from_match_player_stats(
        [
            _row("Ahmed Hassan", "Angola", 22),
            _row("Omar Khaled", "Mali", 9),
        ],
        team="EGY",
    ).drop(columns=["date"])

    result = rank_players(df, metric="points", team="EGY", top_n=2)

    assert "Top 2 EGY players for Points" in result["answer"]
    assert "Ahmed Hassan" in result["answer"]


# ---- script entry point ---------------------------------------------------
def main() -> int:
    tests = [
        test_unified_payload_consumed_without_python_dedup,
        test_union_dedups_same_game_no_double_count,
        test_union_keeps_distinct_games,
        test_union_matchup_token_equivalence_via_match_name,
        test_union_empty_inputs,
        test_adapter_normalizes_api_date_for_analytics,
        test_weighted_score_handles_unified_frame_without_date_column,
        test_rank_metric_handles_unified_frame_without_date_column,
    ]
    failures = 0
    for t in tests:
        try:
            t()
            print(f"[ok ] {t.__name__}")
        except AssertionError as exc:
            failures += 1
            print(f"[FAIL] {t.__name__}: {exc}")
        except Exception as exc:  # pragma: no cover - environment-dependent
            failures += 1
            print(f"[ERR ] {t.__name__}: {exc!r}")
    print()
    if failures:
        print(f"{failures} failure(s).")
        return 1
    print(f"All {len(tests)} Phase 1 union tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
