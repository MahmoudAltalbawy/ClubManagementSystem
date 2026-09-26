"""
Routing regression tests for the hybrid chatbot lanes.

These pin down WHICH lane a question routes to, independent of whether the backing
data (prediction model, app API) is actually reachable. They exercise
``QuestionService._classify_lane`` — a pure mirror of the ``lanes`` tuple in
``ask()`` — so a routing regression is caught here without a live deployment or
manual chat testing.

Every case under BUG_CASES corresponds to a concrete bug the coach reported:

  * "when is the next match"            -> was answered from the PDFs (wrong);
                                           must route to the SCHEDULE lane like
                                           "when is the next event" does.
  * "list the upcoming this week event" -> only returned the next event; must
                                           route to SCHEDULE (window handling is
                                           covered separately in the schedule lane).
  * "recommend a lineup for the next game" -> hit "no box score rows"; the word
                                           "next game" must NOT divert it to the
                                           schedule lane — lineup requests are
                                           prediction-first.

Run directly (``python test_routing_regression.py``) or via pytest.
"""
from __future__ import annotations

import os
import sys

# Make the chatbot package root importable regardless of the caller's CWD.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from services.question_service import QuestionService  # noqa: E402


def classify(question: str) -> str:
    return QuestionService._classify_lane(question)


# ---------------------------------------------------------------------------
# Reported-bug cases: each MUST land in the listed lane.
# ---------------------------------------------------------------------------
BUG_CASES = [
    # "next match/game/event" date questions are SCHEDULE, not prediction/RAG.
    ("when is the next match", "schedule"),
    ("when is the next game", "schedule"),
    ("when is the next event", "schedule"),
    ("when do we play next", "schedule"),
    # "this week" listing must reach the schedule lane (window filtering lives there).
    ("list the upcoming events this week", "schedule"),
    ("what events do we have this week", "schedule"),
    ("what's on the schedule this week", "schedule"),
    # Lineup recommendation is prediction-first even though it says "next game".
    ("recommend a lineup for the next game", "prediction"),
    ("suggest a starting lineup for the next match", "prediction"),
    ("who should start tonight", "prediction"),
]


# ---------------------------------------------------------------------------
# Prediction must still win for genuinely predictive (verb) phrasings, so the
# narrowing of _PREDICTION_PATTERNS did not over-correct.
# ---------------------------------------------------------------------------
PREDICTION_CASES = [
    ("predict the next game", "prediction"),
    ("forecast our performance against Mali", "prediction"),
    ("how will we perform against Angola", "prediction"),
    ("what are our chances of winning the next game", "prediction"),
    ("win probability against Tunisia", "prediction"),
    ("who will win", "prediction"),
]


# ---------------------------------------------------------------------------
# The remaining lanes — guards against future pattern drift / reordering.
# ---------------------------------------------------------------------------
LANE_CASES = [
    # match results (team box scores)
    ("did we beat Mali", "match_results"),
    ("what was the result against Angola", "match_results"),
    ("how did we do against Tunisia", "match_results"),
    ("what is our win-loss record", "match_results"),
    # basketball per-player box-score stats
    ("who are our best 3 point shooters", "basketball_stats"),
    ("top scorer this season", "basketball_stats"),
    ("how many rebounds did Ahmed get", "basketball_stats"),
    ("best free throw shooters", "basketball_stats"),
    # historical lineup on/off analysis (no literal "lineup" word, which would
    # otherwise trip the prediction lane's line-up pattern)
    ("which five played best together", "lineup_analysis"),
    ("show me the on-off splits", "lineup_analysis"),
    # injuries
    ("list active injuries", "injuries"),
    ("who is injured", "injuries"),
    ("who is the most injured player", "injuries"),
    # roster
    ("show me the full roster", "roster"),
    ("who is on the roster", "roster"),
    # physical profile
    ("how tall is Ahmed", "profile"),
    ("who is the heaviest player", "profile"),
    # attendance
    ("show team attendance", "attendance"),
    ("who has the worst turnout", "attendance"),
    # fitness (avoid the word "results", which is a match-results trigger)
    ("what is the team's fitness level", "fitness"),
    ("what is each player's BMI", "fitness"),
    # coaching lineups (saved tactical). NOTE: phrasings containing the literal
    # word "lineup" are intentionally prediction-first, so test this lane via
    # formation / game-model phrasings that don't collide with the lineup pattern.
    ("what is our formation", "coaching_lineups"),
    ("what's our game model", "coaching_lineups"),
    # coach notes
    ("what are the coach notes from the game", "coach_notes"),
    ("show post-game notes", "coach_notes"),
    # plan read
    ("show me the training plan", "plans"),
    # plan improvement (advisory) wins over plain plan read
    ("how can we improve our training plan", "plan_advice"),
    # written match reports -> DB match-report lane (Phase 0), not PDF RAG. These
    # must NOT be stolen by match_results ("result/score") or basketball_stats.
    ("make a report about the Angola game", "match_report"),
    ("write a match report for the Mali game", "match_report"),
    ("give me a report on the Tunisia match", "match_report"),
    ("recap the Uganda game", "match_report"),
]


ALL_CASES = BUG_CASES + PREDICTION_CASES + LANE_CASES


def _run(cases: list[tuple[str, str]]) -> list[str]:
    failures: list[str] = []
    for question, expected in cases:
        actual = classify(question)
        if actual != expected:
            failures.append(f"  {question!r}: expected {expected!r}, got {actual!r}")
    return failures


# ---- pytest entry points --------------------------------------------------
def test_reported_bug_routing() -> None:
    failures = _run(BUG_CASES)
    assert not failures, "Reported-bug routing regressions:\n" + "\n".join(failures)


def test_prediction_still_routes() -> None:
    failures = _run(PREDICTION_CASES)
    assert not failures, "Prediction routing regressions:\n" + "\n".join(failures)


def test_lane_routing() -> None:
    failures = _run(LANE_CASES)
    assert not failures, "Lane routing regressions:\n" + "\n".join(failures)


# ---- script entry point ---------------------------------------------------
def main() -> int:
    failures = _run(ALL_CASES)
    for question, expected in ALL_CASES:
        actual = classify(question)
        flag = "ok " if actual == expected else "FAIL"
        print(f"[{flag}] {question!r:55} -> {actual} (want {expected})")
    print()
    if failures:
        print(f"{len(failures)} routing failure(s):")
        print("\n".join(failures))
        return 1
    print(f"All {len(ALL_CASES)} routing cases passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
