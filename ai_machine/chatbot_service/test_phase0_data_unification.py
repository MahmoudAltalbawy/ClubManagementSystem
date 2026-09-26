"""
Phase 0 data-unification tests.

These pin down the fix for two coach-reported bugs whose root cause was the same:
the analytics / lineup / report paths read ONLY the PDF-derived box-score CSV (or
the PDF RAG index) and ignored the live DB, so:

  * "make a report about the Angola game" was assembled from whatever PDF was
    indexed (a Mali box score) instead of the stored DB match-report; and
  * "suggest a lineup" / stat queries dead-ended on
    "I couldn't find matching box score rows" when only the DB had the numbers.

Covered here:

  * ``box_scores_from_match_player_stats`` — the DB -> box-score DataFrame adapter:
      made/attempted splitting, derived FG, cumulative-row dropping, minutes
      normalisation, and that ``analytics`` enrichment (percentages) runs.
  * adapter output is usable by ``rank_players`` / ``recommend_squad`` (i.e. a DB-only
    team no longer hits the "no box score rows" dead end).
  * report-lane routing — "make a report about X" classifies to ``match_report``.

Run directly (``python test_phase0_data_unification.py``) or via pytest.
"""
from __future__ import annotations

import os
import sys

# Make the chatbot package root importable regardless of the caller's CWD.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from analytics import rank_players, recommend_squad  # noqa: E402
from services.analytics_service import (  # noqa: E402
    _minutes_mmss,
    _split_made_attempted,
    box_scores_from_match_player_stats,
)
from services.question_service import QuestionService  # noqa: E402


# A tiny, realistic get_match_player_stats payload: two per-game rows plus one
# cumulative row that the adapter must drop (so it isn't double-counted).
DB_ROWS = [
    {
        "name": "Ahmed Hassan",
        "opponent_name": "Angola",
        "granularity": "per_game",
        "game_no": 1,
        "minutes": "28:30",
        "two_pt_ma": "6/10",
        "three_pt_ma": "2/5",
        "ft_ma": "4/4",
        "offensive_rebounds": 2,
        "defensive_rebounds": 5,
        "total_rebounds": 7,
        "assists": 4,
        "turnovers": 2,
        "steals": 1,
        "blocks": 0,
        "personal_fouls": 3,
        "fouls_drawn": 2,
        "efficiency": 21,
        "points": 22,
    },
    {
        "name": "Omar Khaled",
        "opponent_name": "Angola",
        "granularity": "per_game",
        "game_no": 1,
        "minutes": "24:00",
        "two_pt_ma": "3/7",
        "three_pt_ma": "1/2",
        "ft_ma": "0/0",
        "offensive_rebounds": 1,
        "defensive_rebounds": 3,
        "total_rebounds": 4,
        "assists": 6,
        "turnovers": 3,
        "steals": 2,
        "blocks": 1,
        "personal_fouls": 1,
        "fouls_drawn": 1,
        "efficiency": 14,
        "points": 9,
    },
    {
        "name": "Ahmed Hassan",
        "opponent_name": None,
        "granularity": "cumulative",  # must be dropped
        "minutes": "52:30",
        "two_pt_ma": "9/17",
        "three_pt_ma": "3/7",
        "ft_ma": "4/4",
        "total_rebounds": 11,
        "assists": 10,
        "points": 31,
        "efficiency": 35,
    },
]


# ---------------------------------------------------------------------------
# made/attempted + minutes helpers
# ---------------------------------------------------------------------------
def test_split_made_attempted() -> None:
    assert _split_made_attempted("20/44") == (20, 44)
    assert _split_made_attempted(" 10 / 25 ") == (10, 25)
    assert _split_made_attempted("7.0/9.0") == (7, 9)
    assert _split_made_attempted("0/0") == (0, 0)
    # Malformed / missing -> (0, 0), never raises.
    assert _split_made_attempted("") == (0, 0)
    assert _split_made_attempted(None) == (0, 0)
    assert _split_made_attempted("5") == (0, 0)
    assert _split_made_attempted("abc/def") == (0, 0)


def test_minutes_mmss() -> None:
    assert _minutes_mmss("28:30") == "28:30"   # already MM:SS -> passthrough
    assert _minutes_mmss(24) == "24:00"         # whole minutes
    assert _minutes_mmss(0) == "0:00"
    assert _minutes_mmss(None) == "0:00"
    assert _minutes_mmss("nonsense") == "0:00"
    # fractional minutes -> seconds
    assert _minutes_mmss(10.5) == "10:30"


# ---------------------------------------------------------------------------
# DB -> box-score adapter
# ---------------------------------------------------------------------------
def test_adapter_shape_and_values() -> None:
    df = box_scores_from_match_player_stats(DB_ROWS, team="EGY")
    # Cumulative row dropped: two per-game rows remain.
    assert len(df) == 2
    for column in (
        "player_name", "team", "fg_made", "fg_attempted", "three_made",
        "three_attempted", "points", "efficiency", "minutes_seconds",
        "three_percentage", "field_goal_percentage",
    ):
        assert column in df.columns, f"missing column {column!r}"

    ahmed = df[df["player_name"] == "Ahmed Hassan"].iloc[0]
    # FG derived from 2PT + 3PT made/attempted: (6+2)/(10+5).
    assert int(ahmed["fg_made"]) == 8
    assert int(ahmed["fg_attempted"]) == 15
    assert int(ahmed["three_made"]) == 2
    assert int(ahmed["ft_made"]) == 4
    # enrich_box_scores computed percentages + minutes_seconds (28:30 -> 1710s).
    assert int(ahmed["minutes_seconds"]) == 28 * 60 + 30
    assert abs(float(ahmed["field_goal_percentage"]) - (8 / 15 * 100)) < 0.5
    assert (df["team"].astype(str).str.upper() == "EGY").all()


def test_adapter_empty_inputs() -> None:
    assert box_scores_from_match_player_stats(None).empty
    assert box_scores_from_match_player_stats([]).empty
    # Only a cumulative row -> nothing usable -> empty (not a crash).
    assert box_scores_from_match_player_stats([{"name": "X", "granularity": "total"}]).empty


# ---------------------------------------------------------------------------
# The whole point: analytics functions work off the DB-derived frame, so a
# DB-only team no longer dead-ends on "I couldn't find matching box score rows".
# ---------------------------------------------------------------------------
def test_rank_players_uses_db_frame() -> None:
    df = box_scores_from_match_player_stats(DB_ROWS, team="EGY")
    result = rank_players(df, metric="points", team="EGY", top_n=5)
    answer = str(result.get("answer") or "")
    assert "couldn't find matching box score" not in answer.lower()
    # Ahmed (22 pts) outranks Omar (9 pts).
    assert "Ahmed" in answer


def test_recommend_squad_uses_db_frame() -> None:
    df = box_scores_from_match_player_stats(DB_ROWS, team="EGY")
    result = recommend_squad(df, team="EGY", top_n=5, strategy="balanced")
    answer = str(result.get("answer") or "")
    assert "couldn't find matching box score" not in answer.lower()
    assert "Ahmed" in answer or "Omar" in answer


# ---------------------------------------------------------------------------
# Report-lane routing (Phase 0): a written "report about X" goes to the DB
# match-report lane, not the PDF RAG fallthrough.
# ---------------------------------------------------------------------------
def test_report_routing() -> None:
    classify = QuestionService._classify_lane
    assert classify("make a report about the Angola game") == "match_report"
    assert classify("write a match report for the Mali game") == "match_report"
    assert classify("recap the Uganda game") == "match_report"
    # A plain results question is NOT a report -> stays in match_results.
    assert classify("what was the result against Angola") == "match_results"
    # A scorer ranking is NOT a report -> basketball_stats.
    assert classify("top scorer this season") == "basketball_stats"


# ---- script entry point ---------------------------------------------------
def main() -> int:
    tests = [
        test_split_made_attempted,
        test_minutes_mmss,
        test_adapter_shape_and_values,
        test_adapter_empty_inputs,
        test_rank_players_uses_db_frame,
        test_recommend_squad_uses_db_frame,
        test_report_routing,
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
    print(f"All {len(tests)} Phase 0 data-unification tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
