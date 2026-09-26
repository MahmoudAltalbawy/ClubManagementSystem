from __future__ import annotations

import os
import re
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from difflib import SequenceMatcher
from pathlib import Path
from typing import Any, Iterator
from uuid import uuid4

import pandas as pd

from analytics import (
    COMPARISON_PATTERNS,
    SUPPORTED_METRICS,
    answer_stat_query,
    available_players_from_df,
    compare_players,
    detect_stat_metric,
    detect_squad_strategy,
    extract_player_names,
    extract_top_n,
    has_any_pattern,
    is_player_opportunity_question,
    normalize_text,
    parse_question,
    parse_stat_question,
    rank_players,
    rank_weighted_score,
    recommend_more_minutes,
    recommend_squad,
    resolve_requested_players,
    summarize_player,
)
from memory_store import is_follow_up

from .analytics_service import (
    box_scores_from_match_player_stats,
    box_scores_union,
    load_project_box_scores,
)
from .cache import _MISS, TTLCache, cache_ttl_seconds, get_team_version
from .groq_client import GroqClient, GroqConfigurationError
from .memory_store import MemoryStore, rewrite_followup_question
from .project_store import ProjectStore
from .rag_service import RagService
from .timing import StageTimer


# Phase 2.5f: cache the assembled per-team box-score frame so it's built once per
# data version instead of once per request. Keyed by the 2.5b team version stamp
# (bumped on ingest) plus the CSV mtime, and bounded by the same TTL as the read
# cache so DB-side changes that don't bump the stamp still age out. Handlers treat
# the returned frame as read-only (analytics functions operate functionally and
# don't mutate their input), which avoids an N-frames-per-request copy under load.
_FRAME_CACHE = TTLCache(cache_ttl_seconds())


def _path_mtime(path: Path) -> float:
    try:
        return path.stat().st_mtime
    except OSError:
        return 0.0


ALLOWED_TYPES = {
    "stat_query",
    "player_comparison",
    "player_summary",
    "analytic_recommendation",
    "player_opportunity_recommendation",
    "squad_recommendation",
    "general_pdf_question",
    "play_by_play_question",
    "lineup_question",
    "plus_minus_question",
    "player_comparison_needs_clarification",
    "clarification",
    "unsupported",
}

ALLOWED_ROUTES = {"analytics", "rag", "clarification", "unsupported"}
SUPPORTED_METRIC_NAMES = set(SUPPORTED_METRICS)

# Coach phrasings that ask the model to project future performance rather than
# report on what already happened in the uploaded box scores. These route to the
# per-team prediction model (PredictionService) BEFORE analytics/RAG, since the
# model's holdout projections answer "what will happen" questions that the
# box-score CSV (a record of the past) cannot.
# IMPORTANT: only PREDICTIVE-VERB phrasings belong here. "next match" / "upcoming
# game" on their own are *schedule* (date) questions, not prediction questions —
# they live in _SCHEDULE_PATTERNS below. A future-tense match reference becomes a
# prediction only when paired with one of these verbs (e.g. "predict the next game",
# "how will we do next match"), which still matches via the verb pattern.
_PREDICTION_PATTERNS = (
    r"\bpredict(?:ion|ions|ed|s)?\b",
    r"\bforecast(?:ed|s|ing)?\b",
    r"\bproject(?:ed|ion|ions)\b",
    r"\bexpected\s+(?:efficiency|performance|points|to\s+perform)\b",
    r"\bhow\s+will\s+we\b",
    r"\bhow\s+(?:do|will)\s+(?:we|you|i)\s+(?:expect|think)\b",
    r"\bwho\s+will\b",
    r"\bgoing\s+to\s+perform\b",
    r"\bwin\s+probability\b",
    r"\bchances?\s+(?:of\s+winning|to\s+win)\b",
)

# Live roster / availability questions that the match PDFs can't answer — these are
# served from the app's current roster (injuries included) via AppDataClient, and only
# when that integration is configured (APP_API_BASE_URL). Otherwise they fall through
# to normal routing.
_ROSTER_PATTERNS = (
    r"\binjur(?:y|ies|ed)\b",
    r"\bavailab(?:le|ility)\b",
    r"\bwho\s+is\s+out\b",
    r"\bwho'?s\s+out\b",
    r"\bsidelined\b",
    r"\bfit\s+to\s+play\b",
    r"\bon\s+the\s+roster\b",
    r"\bcurrent\s+roster\b",
    r"\bfull\s+roster\b",
    r"\bwho\s+can\s+play\b",
)

# Lineup-suggestion phrasings. Per the prediction-first rule, "suggest a lineup" /
# "who should start" are answered by the prediction model (filtered by live
# availability) BEFORE the box-score squad analytics. If no model is trained yet,
# the lane returns None and we fall through to analytics squad recommendation.
_LINEUP_PATTERNS = (
    r"\b(?:suggest|recommend|pick|build|set|choose|propose)\b[\w\s]{0,25}\b(?:line-?up|squad|squat)\b",
    r"\bstarting\s+(?:five|line-?up|squad|5)\b",
    r"\bwho\s+should\s+start\b",
    r"\bbest\s+(?:line-?up|starting|squad|squat)\b",
    r"\bline-?up\b",
    r"\b(?:game|match|team)\s+(?:squad|squat)\b",
)

# Upcoming schedule / fixture / "when do we play" questions. These are date
# questions the box scores can't answer; served from the app's events. "next
# match/game" is intentionally excluded so prediction-phrased questions still
# route to the model.
_SCHEDULE_PATTERNS = (
    r"\bschedule\b",
    r"\bfixtures?\b",
    r"\bcalendar\b",
    r"\bwhen\s+(?:is|are|do|does|'?s|will)\b",
    r"\bwhat\s+time\b",
    r"\bupcoming\s+(?:events?|training|practice|fixtures?|sessions?|schedule|match(?:es)?|games?)\b",
    r"\bnext\s+(?:training|practice|event|meeting|session|match|game|fixture)\b",
    r"\bthis\s+(?:week|weekend|month)\b",
    r"\bevents?\b",
    r"\bwhat'?s\s+(?:on|coming\s+up|planned)\b",
    r"\bwhat\s+(?:is|are)\s+(?:on|coming\s+up|planned)\b",
)

# Injury-detail questions. The roster lane gives a flat injured/available list; this
# lane surfaces per-injury diagnosis, expected return date, and recovery tips, and can
# rank "most injured". Tried BEFORE roster so injury-specific phrasings get the richer
# answer; falls through to roster (then RAG) if injuries data is unavailable.
_INJURY_PATTERNS = (
    r"\binjur(?:y|ies|ed)\b",
    r"\bsidelined\b",
    r"\brecover(?:y|ing|ed)?\b",
    r"\brecovery\s+tips?\b",
    r"\bdiagnos(?:is|ed|es)\b",
    r"\breturn\s+date\b",
    r"\bback\s+(?:to|in)\s+(?:play|training|action|the\s+game)\b",
    r"\bwhen\s+(?:will|is|are|can)\b[\w\s]{0,25}\bback\b",
    r"\bhow\s+long\b[\w\s]{0,25}\bout\b",
    r"\bmost\s+injured\b",
    r"\bwho\s+is\s+out\b",
    r"\bwho'?s\s+out\b",
)

# Player physical-profile questions (height / weight). Served from the live roster,
# which now carries height and weight per player.
_PROFILE_PATTERNS = (
    r"\bheight\b",
    r"\bweight\b",
    r"\bhow\s+tall\b",
    r"\bhow\s+heavy\b",
    r"\btallest\b",
    r"\bshortest\b",
    r"\bheaviest\b",
    r"\blightest\b",
    r"\bbody\s+measurements?\b",
)

# Attendance / who's showing up to sessions.
_ATTENDANCE_PATTERNS = (
    r"\battendance\b",
    r"\bwho\s+(?:is|'?s)\s+(?:missing|absent)\b",
    r"\bmissed\s+(?:training|practice|sessions?)\b",
    r"\bturnout\b",
    r"\bshowing\s+up\b",
)

# Fitness / physical condition.
_FITNESS_PATTERNS = (
    r"\bfitness\b",
    r"\bbmi\b",
    r"\bbody\s+fat\b",
    r"\bendurance\b",
    r"\bspeed\s+test\b",
    r"\bphysical\s+(?:condition|test|state)\b",
)

# Coaching-plan lookup (read). Explicit phrasings only, to avoid false positives.
_PLAN_PATTERNS = (
    r"\b(?:training|coaching|game|practice)\s+plan\b",
    r"\bour\s+plan\b",
    r"\bthe\s+plan\b",
    r"\bcurrent\s+plan\b",
    r"\bshow\b[\w\s]{0,15}\bplan\b",
)

# Plan-improvement (advisory/synthesis). Combined with a plan reference, these
# route to the multi-source advisory lane rather than a plain plan read.
_PLAN_IMPROVE_PATTERNS = (
    r"\bimprove\b",
    r"\bbetter\b",
    r"\boptimi[sz]e\b",
    r"\bstronger\b",
    r"\benhance\b",
    r"\bwhich\s+plan\b",
    r"\brefine\b",
    r"\btweak\b",
    r"\badjust\b",
)

# Basketball box-score questions (per-player). These ask about real basketball
# numbers — points, threes, rebounds, assists, steals, blocks, etc. — which live in
# the PDF-imported PlayerMatchStats table, exposed via match-player-stats. Served
# BEFORE analytics so a specific metric question ("best 3-point shooters") gets a
# specific ranked answer instead of a broad summary. No-op unless the app API is on.
_BASKETBALL_STAT_PATTERNS = (
    r"\b(?:three|3)[\s-]?(?:point|pt|pointers?)\b",
    r"\bfrom\s+(?:deep|downtown|beyond\s+the\s+arc|three)\b",
    r"\b(?:two|2)[\s-]?(?:point|pt)\b",
    r"\bfree[\s-]?throws?\b",
    r"\bft\s+(?:percentage|pct|shooting)\b",
    r"\b(?:top|best|leading|most)\s+(?:scorer|scorers|scoring)\b",
    r"\bpoints?\s+per\s+game\b",
    r"\bppg\b|\brpg\b|\bapg\b|\bspg\b|\bbpg\b",
    r"\brebound(?:s|er|ers|ing)?\b",
    r"\bboards\b",
    r"\bassist(?:s)?\b",
    r"\bsteal(?:s)?\b",
    r"\bblock(?:s|ed|er|ers)?\b",
    r"\bturnover(?:s)?\b|\bgiveaways?\b",
    r"\befficiency\b",
    r"\bfoul(?:s)?\b",
    r"\bbox\s*score\b",
    r"\bshoot(?:s|er|ers|ing)?\b",
    r"\bscored?\b|\bscoring\b",
    r"\bhighest\s+scoring\b",
)

# "Did we win / last result / our record" — answered from team box scores
# (match-team-stats) rather than the prediction model or RAG.
_MATCH_RESULT_PATTERNS = (
    r"\bdid\s+we\s+(?:win|lose|beat)\b",
    r"\bwho\s+did\s+we\s+(?:beat|play|lose\s+to)\b",
    r"\blast\s+(?:game|match)\b",
    r"\bprevious\s+(?:game|match)\b",
    r"\b(?:our|the\s+team'?s?)\s+record\b",
    r"\bwin[\s-]?loss\b",
    r"\bhow\s+many\s+(?:games|matches)\s+(?:have\s+we\s+|did\s+we\s+)?(?:won|lost|played)\b",
    r"\bresults?\b",
    r"\bhow\s+did\s+we\s+(?:do|play|perform)\s+(?:against|vs)\b",
    r"\bfinal\s+score\b",
    r"\bscore(?:line)?\s+(?:against|vs)\b",
)

# Written match-report requests ("make a report about the Angola game"). Routes to
# the DB match-reports lane BEFORE the PDF RAG so a report is built from the team's
# stored analysis rather than from whatever PDF happens to be indexed (the bug where
# an "Angola report" was assembled from the only PDF on hand, a Mali box score).
_REPORT_PATTERNS = (
    r"\b(?:match|game|post[\s-]?game|scouting|team)\s+report\b",
    r"\breport\s+(?:about|on|for|of|covering)\b",
    r"\b(?:make|write|create|generate|produce|prepare|give\s+me|put\s+together|draft)\b[^.?!]*\breport\b",
    r"\b(?:recap|write[\s-]?up|breakdown|summary|summarise|summarize)\b[^.?!]*\b(?:game|match|fixture)\b",
    r"\b(?:game|match)\b[^.?!]*\b(?:recap|write[\s-]?up|breakdown|summary)\b",
)

# Historical lineup on/off splits ("which five played best together"). Distinct
# from the prediction lane's "suggest a lineup" — this reports what already happened.
_LINEUP_ANALYSIS_PATTERNS = (
    r"\b(?:best|most\s+effective|top|strongest)\s+(?:five|5|lineup|line-?up|unit|combination)\b",
    r"\bwhich\s+(?:five|5|lineup|line-?up|unit|combination)\b",
    r"\blineup\s+(?:analysis|combinations?|splits?|stats?)\b",
    r"\bon[\s/-]?off\b",
    r"\bplus[\s/-]?minus\b",
    r"\bnet\s+rating\b",
    r"\bplayed\s+(?:best\s+)?together\b",
    r"\btime\s+on\s+court\b",
)

# Saved tactical lineups (coaching context): formation / game model / starting unit.
_COACHING_LINEUP_PATTERNS = (
    r"\bformation\b",
    r"\bgame\s+model\b",
    r"\btactical\s+(?:lineup|line-?up|notes|setup|plan)\b",
    r"\bstarting\s+(?:unit|formation)\b",
    r"\bour\s+(?:saved\s+)?lineups?\b",
    r"\bset\s+(?:plays?|pieces?)\b",
)

# Coach notes attached to games.
_COACH_NOTES_PATTERNS = (
    r"\bcoach(?:'?s)?\s+notes?\b",
    r"\bgame\s+notes?\b",
    r"\bnotes?\s+(?:from|on|about)\s+the\s+(?:game|match)\b",
    r"\bwhat\s+did\s+the\s+coach\s+(?:say|note|write)\b",
    r"\bpost[\s-]?game\s+(?:notes?|comments?|remarks?)\b",
)

# Maps a basketball metric to (display label, kind, field). kind="int" sums a
# numeric column; kind="ratio" parses a "made/attempted" string. Order matters:
# the FIRST entry whose any-pattern matches the question wins, so put specific
# shot types (three/two/free-throw) before the generic "points"/"shoot".
_BB_METRICS: tuple[tuple[str, str, str, tuple[str, ...]], ...] = (
    ("three-pointers", "ratio", "three_pt_ma",
     (r"\b(?:three|3)[\s-]?(?:point|pt|pointers?)\b", r"\bfrom\s+(?:deep|downtown|beyond\s+the\s+arc)\b", r"\btreys?\b")),
    ("two-pointers", "ratio", "two_pt_ma",
     (r"\b(?:two|2)[\s-]?(?:point|pt)\b",)),
    ("free throws", "ratio", "ft_ma",
     (r"\bfree[\s-]?throws?\b", r"\bft\s+(?:percentage|pct|shooting|made)\b", r"\bfoul\s+shots?\b")),
    ("offensive rebounds", "int", "offensive_rebounds",
     (r"\boffensive\s+rebounds?\b", r"\bo[\s-]?rebs?\b")),
    ("defensive rebounds", "int", "defensive_rebounds",
     (r"\bdefensive\s+rebounds?\b", r"\bd[\s-]?rebs?\b")),
    ("rebounds", "int", "total_rebounds",
     (r"\brebound(?:s|er|ers|ing)?\b", r"\bboards\b", r"\brpg\b")),
    ("assists", "int", "assists",
     (r"\bassist(?:s)?\b", r"\bapg\b", r"\bplaymak(?:er|ers|ing)\b")),
    ("steals", "int", "steals",
     (r"\bsteal(?:s)?\b", r"\bspg\b")),
    ("blocks", "int", "blocks",
     (r"\bblock(?:s|ed|er|ers)?\b", r"\bbpg\b")),
    ("turnovers", "int", "turnovers",
     (r"\bturnover(?:s)?\b", r"\bgiveaways?\b")),
    ("efficiency", "int", "efficiency",
     (r"\befficiency\b", r"\bmost\s+efficient\b")),
    ("personal fouls", "int", "personal_fouls",
     (r"\bfoul(?:s)?\b",)),
    ("points", "int", "points",
     (r"\bpoint(?:s)?\b", r"\bscorers?\b", r"\bscoring\b", r"\bscored?\b", r"\bppg\b", r"\bshoot(?:s|er|ers|ing)?\b")),
)

SUPPORTED_ANALYTICS_RECIPES = {
    "rank_by_metric",
    "player_comparison",
    "player_summary",
    "opportunity_score",
    "weighted_score",
    "balanced_impact_score",
    "assist_turnover_context",
    "unsupported",
}


@dataclass(frozen=True)
class ProjectContext:
    project_id: str
    available_teams: list[str]
    original_question: str | None = None
    groq_client: Any | None = None


def _env_enabled(name: str, default: str = "0") -> bool:
    return os.getenv(name, default).lower() in {"1", "true", "yes", "on"}


def _env_int(name: str, default: int, minimum: int = 1) -> int:
    """Read a positive int from env, clamped to ``minimum``. Bad values fall back to
    ``default`` rather than raising — config typos must not break a request."""
    try:
        value = int(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        return default
    return max(minimum, value)


def _as_list(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []
    return [str(item).strip() for item in value if str(item).strip()]


def _int_or_default(value: Any, default: int) -> int:
    try:
        return max(1, int(value))
    except (TypeError, ValueError):
        return default


def _nonnegative_int(value: Any, default: int = 0) -> int:
    try:
        return max(0, int(value))
    except (TypeError, ValueError):
        return default


def _normalise_route(value: Any) -> str | None:
    route = str(value or "").strip().lower()
    return route if route in ALLOWED_ROUTES else None


def _normalise_type(value: Any) -> str | None:
    intent_type = str(value or "").strip()
    return intent_type if intent_type in ALLOWED_TYPES else None


def _normalise_metric(value: Any, question: str, deterministic: dict[str, Any]) -> str | None:
    detected = detect_stat_metric(question)
    raw = str(value or "").strip().lower().replace("-", "_").replace(" ", "_")
    aliases = {
        "pts": "points",
        "reb": "rebounds",
        "rebs": "rebounds",
        "boards": "rebounds",
        "oreb": "offensive_rebounds",
        "dreb": "defensive_rebounds",
        "ast": "assists",
        "asts": "assists",
        "stl": "steals",
        "stls": "steals",
        "steel": "steals",
        "steels": "steals",
        "2pt": "two_point_shooting",
        "2pts": "two_point_shooting",
        "two_point": "two_point_shooting",
        "two_pointers": "two_point_shooting",
        "3pt": "three_point_shooting",
        "3pts": "three_point_shooting",
        "three_point": "three_point_shooting",
        "threes": "three_point_shooting",
        "ft": "free_throw_percentage",
        "ft_percent": "free_throw_percentage",
        "ft_percentage": "free_throw_percentage",
        "fg_percent": "field_goal_percentage",
        "fg_percentage": "field_goal_percentage",
        "plus_minus": "plus_minus",
    }
    metric = aliases.get(raw, raw)
    if detected:
        metric = detected
    if metric in SUPPORTED_METRIC_NAMES:
        return metric
    fallback = deterministic.get("metric")
    return str(fallback) if fallback in SUPPORTED_METRIC_NAMES else None


def _match_players(players: list[str], available_players: list[str]) -> list[str]:
    if not players:
        return []
    resolved = resolve_requested_players(players, available_players)
    if resolved:
        return resolved
    return extract_player_names(" ".join(players), available_players)


def _available_teams_from_df(df: pd.DataFrame, default_team: str) -> list[str]:
    teams = [default_team]
    if "team" in df.columns:
        teams.extend(df["team"].dropna().astype(str).str.upper().unique().tolist())
    if "opponent" in df.columns:
        teams.extend(df["opponent"].dropna().astype(str).str.upper().unique().tolist())
    return sorted({team for team in teams if team})


def _available_columns_from_df(df: pd.DataFrame) -> list[str]:
    return sorted(str(column) for column in df.columns)


def _plan_dict(value: Any) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _recipe_name(recipe: dict[str, Any]) -> str:
    return str(recipe.get("name") or "").strip()


def _normalise_metric_list(values: Any, question: str, deterministic: dict[str, Any]) -> list[str]:
    raw_values = _as_list(values)
    metrics: list[str] = []
    for value in raw_values:
        metric = _normalise_metric(value, question, deterministic)
        if metric:
            metrics.append(metric)
    return list(dict.fromkeys(metrics))


def _normalise_analytics_recipe(
    intent: dict[str, Any],
    intent_type: str,
    metric: str | None,
    metrics: list[str],
) -> dict[str, Any]:
    recipe = dict(_plan_dict(intent.get("analytics_recipe")))
    name = _recipe_name(recipe)
    aliases = {
        "rank": "rank_by_metric",
        "ranking": "rank_by_metric",
        "metric_ranking": "rank_by_metric",
        "opportunity": "opportunity_score",
        "more_minutes": "opportunity_score",
        "underused_high_impact": "opportunity_score",
        "balanced": "balanced_impact_score",
        "squad": "balanced_impact_score",
        "lineup": "balanced_impact_score",
        "assist_turnover_ratio": "assist_turnover_context",
        "assist_to_turnover": "assist_turnover_context",
    }
    name = aliases.get(name, name)
    if name not in SUPPORTED_ANALYTICS_RECIPES:
        if intent_type == "player_comparison":
            name = "player_comparison"
        elif intent_type == "player_summary":
            name = "player_summary"
        elif intent_type in {"squad_recommendation"}:
            name = "balanced_impact_score"
        elif intent_type in {"analytic_recommendation", "player_opportunity_recommendation"}:
            name = "opportunity_score"
        elif metric:
            name = "rank_by_metric"
        else:
            name = "unsupported"
    recipe["name"] = name
    if metric and not recipe.get("ranking_metric"):
        recipe["ranking_metric"] = metric
    if metrics and not recipe.get("metrics"):
        recipe["metrics"] = metrics
    if not recipe.get("sort"):
        recipe["sort"] = "desc"
    if not isinstance(recipe.get("filters"), dict):
        recipe["filters"] = {}
    return recipe


def _unsupported_answer(parsed: dict[str, Any]) -> str:
    recipe = _plan_dict(parsed.get("analytics_recipe"))
    missing = (
        parsed.get("missing_data")
        or recipe.get("missing_data")
        or parsed.get("reason")
        or "the requested data"
    )
    return f"I can't answer that from the uploaded reports because the required data is not available: {missing}."


def _comparison_fragments(question: str) -> list[str]:
    cleaned = question.strip()
    cleaned = re.sub(r"\bwho\s+is\s+better\s+between\b", "", cleaned, flags=re.IGNORECASE)
    cleaned = re.sub(r"\bdifference\s+between\b", "", cleaned, flags=re.IGNORECASE)
    cleaned = re.sub(r"\bcompare\b|\bcomparison\b", "", cleaned, flags=re.IGNORECASE)
    cleaned = re.sub(r"\bversus\b|\bvs\.?\b", " and ", cleaned, flags=re.IGNORECASE)
    cleaned = re.split(r"\b(?:in|for|on)\b", cleaned, maxsplit=1, flags=re.IGNORECASE)[0]
    fragments = re.split(r"\s+\band\b\s+|,|/", cleaned, flags=re.IGNORECASE)
    result: list[str] = []
    for fragment in fragments:
        fragment = re.sub(r"[^A-Za-z .'-]", " ", fragment)
        fragment = " ".join(fragment.split())
        if len(fragment.split()) >= 2:
            result.append(" ".join(part.capitalize() for part in fragment.split()))
    return result


def _best_player_suggestion(fragment: str, available_players: list[str]) -> str | None:
    fragment_norm = normalize_text(fragment)
    best_player = None
    best_score = 0.0
    for player in available_players:
        score = SequenceMatcher(None, fragment_norm, normalize_text(player)).ratio()
        if score > best_score:
            best_player = player
            best_score = score
    return best_player if best_player and best_score >= 0.55 else None


def _missing_comparison_names(
    question: str,
    matched_players: list[str],
    available_players: list[str],
) -> tuple[list[str], dict[str, str]]:
    fragments = _comparison_fragments(question)
    missing: list[str] = []
    suggestions: dict[str, str] = {}
    matched_norms = [normalize_text(player) for player in matched_players]
    for fragment in fragments:
        fragment_norm = normalize_text(fragment)
        if any(fragment_norm in player_norm or player_norm in fragment_norm for player_norm in matched_norms):
            continue
        missing.append(fragment)
        suggestion = _best_player_suggestion(fragment, available_players)
        if suggestion:
            suggestions[fragment] = suggestion
    return missing, suggestions


def _clarification_answer(parsed: dict[str, Any]) -> str:
    clarification_question = str(parsed.get("clarification_question") or "").strip()
    if clarification_question:
        return clarification_question

    found = _as_list(parsed.get("players"))
    missing = _as_list(parsed.get("missing_players"))
    suggestions = parsed.get("suggestions") if isinstance(parsed.get("suggestions"), dict) else {}

    found_text = ""
    if found:
        found_text = "I found " + " and ".join(found) + ", but "
    else:
        found_text = "I "

    if len(missing) == 1:
        missing_name = missing[0]
        suggestion = suggestions.get(missing_name)
        if suggestion:
            return f"{found_text}could not confidently match '{missing_name}'. Did you mean {suggestion}?"
        return f"{found_text}could not confidently match '{missing_name}'. Please send the exact player name from the uploaded reports."

    if not missing:
        return str(parsed.get("reason") or "Can you clarify what you want to compare or rank?")

    lines = [f"{found_text}could not confidently match these players: {', '.join(missing)}."]
    suggested = [f"{name} -> {suggestion}" for name, suggestion in suggestions.items()]
    if suggested:
        lines.append("Did you mean: " + "; ".join(suggested) + "?")
    return " ".join(lines)


def classify_question_with_groq_first(
    question: str,
    project_context: ProjectContext,
    available_players: list[str],
    default_team: str,
    memory_context: list[dict[str, Any]] | None = None,
    available_columns: list[str] | None = None,
) -> dict[str, Any]:
    service = QuestionService(groq_client=project_context.groq_client or GroqClient())
    return service._classify_question_with_groq_first(
        question=question,
        project_context=project_context,
        available_players=available_players,
        default_team=default_team,
        memory_context=memory_context,
        available_columns=available_columns,
    )


def plan_question_with_groq(
    question: str,
    available_players: list[str],
    available_teams: list[str],
    available_columns: list[str],
    previous_memory: list[dict[str, Any]] | None = None,
    groq_client: Any | None = None,
    default_team: str = "EGY",
) -> dict[str, Any] | None:
    client = groq_client or GroqClient()
    service = QuestionService(groq_client=client)
    project_context = ProjectContext(
        project_id="",
        available_teams=available_teams,
        original_question=question,
        groq_client=client,
    )
    return service._plan_question_with_groq(
        question=question,
        project_context=project_context,
        available_players=available_players,
        available_columns=available_columns,
        default_team=default_team,
        memory_context=previous_memory,
    )


def execute_analytics_recipe(df: pd.DataFrame, plan: dict[str, Any]) -> dict[str, Any]:
    return QuestionService()._execute_analytics_recipe(df, plan, default_team=str(plan.get("team") or "EGY"))


class QuestionService:
    def __init__(
        self,
        store: ProjectStore | None = None,
        groq_client: GroqClient | None = None,
        enable_chroma_memory: bool = True,
    ) -> None:
        self.store = store or ProjectStore()
        self.groq_client = groq_client or GroqClient()
        self.enable_chroma_memory = enable_chroma_memory

    def ask(
        self,
        project_id: str,
        question: str,
        team: str = "EGY",
        session_id: str | None = None,
        debug: bool = False,
        pdf_scope: str = "team",
    ) -> dict[str, Any]:
        # Phase 2-pre: per-stage latency timing. Enabled by DEBUG_TIMINGS, or forced on
        # for an explicit debug=True call so a coach can inspect timings ad hoc. No-op
        # (and absent from the response) otherwise, so prod pays nothing.
        timer = StageTimer.from_env(override=True if debug else None)

        self.store.ensure_project(project_id)
        # Resolve the project's real box-score team code (Phase 1). No-op until the
        # .NET team endpoint ships: get_team returns None -> keeps the caller's team.
        team = self._project_team_code(project_id, team)
        session_id = session_id or str(uuid4())
        memory_store = MemoryStore(
            db_path=self.store.chat_db_path(project_id),
            chroma_dir=self.store.chroma_chat_memory_dir(project_id),
            enable_chroma=self.enable_chroma_memory,
            project_id=project_id,
        )

        # Phase 2d: box scores (CSV read + possible .NET round trip) are independent of
        # the memory/rewrite steps — classify needs both, but neither needs the other —
        # so kick off the load on a worker thread to overlap it with the rewrite LLM
        # call instead of running them back to back.
        _bs_executor, _bs_future = self._spawn_box_scores(project_id, team)
        try:
            with timer.stage("memory_retrieve"):
                semantic_memories = memory_store.retrieve_similar(session_id, question, top_k=3)
                chronological_messages = memory_store.recent_messages(session_id, limit=6)
            rewritten_question, deterministic_rewrite = rewrite_followup_question(
                question,
                semantic_memories,
                chronological_messages=chronological_messages,
            )
            groq_rewrite = False
            if not deterministic_rewrite and chronological_messages:
                with timer.stage("rewrite_groq"):
                    rewritten_question, groq_rewrite = self._rewrite_followup_with_groq(
                        question,
                        chronological_messages,
                        default_team=team,
                    )

            with timer.stage("box_scores"):
                try:
                    df = _bs_future.result()
                except Exception:
                    df = pd.DataFrame()
        finally:
            _bs_executor.shutdown(wait=False)
        available_players = available_players_from_df(df, team)
        memory_context = [
            {
                "role": getattr(message, "role", ""),
                "content": getattr(message, "content", ""),
                "parsed_intent": getattr(message, "parsed_intent", {}),
                "route": getattr(message, "route", ""),
                "metric": getattr(message, "metric", ""),
                "players": getattr(message, "players", []),
            }
            for message in chronological_messages[-6:]
        ]
        memory_context.extend(semantic_memories[:3])
        # Phase 2b: if a deterministic regex lane already claims this question, its
        # handler will produce the answer and the Groq classifier output would be
        # discarded — so skip that LLM round trip. Gated by SKIP_GROQ_CLASSIFIER_ON_LANE_MATCH
        # (default on). The lane is re-detected here from the same rewritten_question the
        # lanes loop uses below, so the decision is consistent.
        lane_match = self._classify_lane(rewritten_question)
        allow_groq_classifier = not (
            lane_match != "classifier"
            and _env_enabled("SKIP_GROQ_CLASSIFIER_ON_LANE_MATCH", "1")
        )
        with timer.stage("classify"):
            parsed = self.classify_question(
                rewritten_question,
                df,
                available_players,
                default_team=team,
                memory_context=memory_context,
                original_question=question,
                project_id=project_id,
                allow_groq=allow_groq_classifier,
            )
        route = str(parsed.get("route") or "rag")
        query_type = str(parsed.get("type") or "general_pdf_question")
        metric = parsed.get("metric")
        metrics = _as_list(parsed.get("metrics"))
        players = _as_list(parsed.get("players"))
        sources: list[Any] = []
        retrieval_engine: str | None = None

        # ---- Document-only scope ("ask this PDF only") -----------------------
        # When the coach uploaded a PDF to THIS chat (scope=session) and asks with
        # pdf_scope="session", answer strictly from that session's isolated index —
        # bypassing the hybrid lanes, the team box-score analytics, and the team PDF
        # corpus entirely. Falls back to normal routing if no session index exists.
        doc_only = (
            str(pdf_scope or "team").strip().lower() == "session"
            and self.store.session_has_index(project_id, session_id)
        )

        # Phase 2-pre: time the answer-producing block (lanes + RAG/analytics + final
        # LLM generation) as one "answer" stage. Marked rather than wrapped to keep the
        # branch indentation unchanged.
        _answer_t0 = time.perf_counter()
        if doc_only:
            rag = RagService(
                chunks_csv=self.store.session_chunks_csv(project_id, session_id),
                chroma_dir=self.store.session_chroma_dir(project_id, session_id),
                groq_client=self.groq_client,
            )
            answer, retrieval_engine, sources = rag.answer(rewritten_question, query_type=query_type)
            route = "document_session"
            query_type = "general_pdf_question"
        else:
            # ---- Hybrid lanes (Phase 9) --------------------------------------
            # Tried in priority order before the box-score/RAG classifier route. Each
            # returns (answer, sources) or None to fall through, and is a no-op unless
            # its data source is reachable (prediction model trained, or app API
            # configured), so a minimal deployment degrades to classic routing.
            #
            # Order encodes intent priority:
            #   1. plan-improvement  -> multi-source advisory synthesis
            #   2. schedule          -> date/fixture questions
            #   3. prediction        -> "predict" / "suggest a lineup" (model-first)
            #   4. match-results     -> "did we win" / record (team box scores)
            #   5. basketball-stats  -> "best 3pt shooters" / scorer ranking (player box scores)
            #   6. lineup-analysis   -> historical on/off splits
            #   7. injuries, 8. roster, 9. profile
            #   10. attendance, 11. fitness
            #   12. coaching-lineups, 13. coach-notes, 14. plan (read)
            hybrid_answer: str | None = None
            hybrid_sources: list[Any] = []
            hybrid_route: str | None = None

            lanes = (
                (self._looks_like_plan_improvement, lambda: self._answer_plan_advice(project_id, rewritten_question, team), "plan_advice"),
                (self._looks_like_schedule, lambda: self._answer_schedule(project_id, rewritten_question), "schedule"),
                (self._looks_like_prediction, lambda: self._answer_predictions(project_id, rewritten_question, team), "prediction"),
                (self._looks_like_match_results, lambda: self._answer_match_results(project_id, rewritten_question), "match_results"),
                (self._looks_like_basketball_stats, lambda: self._answer_basketball_player_stats(project_id, rewritten_question), "basketball_stats"),
                (self._looks_like_lineup_analysis, lambda: self._answer_lineup_analysis(project_id, rewritten_question), "lineup_analysis"),
                (self._looks_like_injuries, lambda: self._answer_injuries(project_id, rewritten_question), "injuries"),
                (self._looks_like_roster, lambda: self._answer_roster(project_id, rewritten_question), "roster"),
                (self._looks_like_profile, lambda: self._answer_profile(project_id, rewritten_question), "profile"),
                (self._looks_like_attendance, lambda: self._answer_attendance(project_id, rewritten_question), "attendance"),
                (self._looks_like_fitness, lambda: self._answer_fitness(project_id, rewritten_question), "fitness"),
                (self._looks_like_coaching_lineups, lambda: self._answer_coaching_lineups(project_id, rewritten_question), "coaching_lineups"),
                (self._looks_like_coach_notes, lambda: self._answer_coach_notes(project_id, rewritten_question), "coach_notes"),
                (self._looks_like_plan, lambda: self._answer_plans(project_id, rewritten_question), "plans"),
                # Last lane before the PDF RAG fallthrough: a generic "make a report
                # about the X game" is answered from stored DB match-reports first, so
                # it isn't assembled from an unrelated PDF. Falls through (returns None)
                # when no DB report matches, letting RAG + the opponent guard handle it.
                (self._looks_like_report, lambda: self._answer_match_report(project_id, rewritten_question), "match_report"),
            )
            for detector, handler, lane_route in lanes:
                if not detector(rewritten_question):
                    continue
                result = handler()
                if result is not None:
                    hybrid_answer, hybrid_sources = result
                    hybrid_route = lane_route
                    break

            if hybrid_answer is not None:
                answer, sources, route, query_type = hybrid_answer, hybrid_sources, hybrid_route, hybrid_route
            elif route == "unsupported":
                answer = _unsupported_answer(parsed)
            elif route == "clarification":
                answer = _clarification_answer(parsed)
            elif route == "analytics":
                answer, sources = self._answer_analytics(parsed, df, project_id, rewritten_question, team)
            else:
                rag = RagService(
                    chunks_csv=self.store.chunks_csv(project_id),
                    chroma_dir=self.store.chroma_pdf_index_dir(project_id),
                    groq_client=self.groq_client,
                )
                answer, retrieval_engine, sources = rag.answer(rewritten_question, query_type=query_type)

        timer.mark("answer", (time.perf_counter() - _answer_t0) * 1000.0)

        memory_store.save_message(session_id, "user", question, parsed, route, str(metric or ""), players)
        memory_store.save_message(session_id, "assistant", answer, parsed, route, str(metric or ""), players)

        response = {
            "project_id": project_id,
            "session_id": session_id,
            "answer": answer,
            "type": query_type,
            "route": route,
            "metric": metric,
            "metrics": metrics,
            "players": players,
            "sources": sources,
            "original_question": question,
            "rewritten_question": rewritten_question,
            "retrieval_engine": retrieval_engine,
            "classification_source": parsed.get("_classification_source", "fallback"),
            "team": parsed.get("team"),
            "top_n": parsed.get("top_n"),
            "strategy": parsed.get("strategy"),
            "analytics_recipe": parsed.get("analytics_recipe"),
        }
        # Phase 2-pre: emit the per-stage breakdown to logs and expose it on the
        # response only when timing is on (DEBUG_TIMINGS or debug=True). as_dict() is
        # empty when disabled, so the field never leaks into prod responses.
        timer.log(context=f"route={route}")
        if timer.enabled:
            response["timings"] = timer.as_dict()
        if debug:
            response.update(
                {
                    "semantic_memory_used": bool(semantic_memories),
                    "deterministic_rewrite": deterministic_rewrite,
                    "groq_rewrite": groq_rewrite,
                    "team": parsed.get("team"),
                    "top_n": parsed.get("top_n"),
                    "strategy": parsed.get("strategy"),
                    "analytics_recipe": parsed.get("analytics_recipe"),
                    "groq_plan": parsed.get("_groq_plan"),
                    "fallback_reason": parsed.get("fallback_reason"),
                    "metrics": metrics,
                    "parsed_intent": parsed,
                }
            )
        return response

    def ask_stream(
        self,
        project_id: str,
        question: str,
        team: str = "EGY",
        session_id: str | None = None,
        pdf_scope: str = "team",
    ) -> Iterator[dict[str, Any]]:
        """Streaming sibling of ask() (Phase 2a). Yields event dicts:

          {"event": "meta",  "data": {...}}            once, up front (ids, rewritten q)
          {"event": "token", "data": "<text>"}         zero or more, the answer forming
          {"event": "done",  "data": {full response}}  once, at the end

        Only the PDF-RAG answer is truly token-streamed (its LLM generation is the slow
        part); deterministic / DB / analytics routes produce a complete string cheaply
        and are emitted as a single token event. The transcript is persisted after the
        answer is fully assembled, exactly as ask() does. This mirrors ask()'s routing —
        the two must be kept in sync.
        """
        self.store.ensure_project(project_id)
        team = self._project_team_code(project_id, team)
        session_id = session_id or str(uuid4())
        memory_store = MemoryStore(
            db_path=self.store.chat_db_path(project_id),
            chroma_dir=self.store.chroma_chat_memory_dir(project_id),
            enable_chroma=self.enable_chroma_memory,
            project_id=project_id,
        )

        # Phase 2d: overlap the box-score load with memory/rewrite (see ask()).
        _bs_executor, _bs_future = self._spawn_box_scores(project_id, team)
        try:
            semantic_memories = memory_store.retrieve_similar(session_id, question, top_k=3)
            chronological_messages = memory_store.recent_messages(session_id, limit=6)
            rewritten_question, deterministic_rewrite = rewrite_followup_question(
                question,
                semantic_memories,
                chronological_messages=chronological_messages,
            )
            if not deterministic_rewrite and chronological_messages:
                rewritten_question, _ = self._rewrite_followup_with_groq(
                    question,
                    chronological_messages,
                    default_team=team,
                )
            try:
                df = _bs_future.result()
            except Exception:
                df = pd.DataFrame()
        finally:
            _bs_executor.shutdown(wait=False)
        available_players = available_players_from_df(df, team)
        memory_context = [
            {
                "role": getattr(message, "role", ""),
                "content": getattr(message, "content", ""),
                "parsed_intent": getattr(message, "parsed_intent", {}),
                "route": getattr(message, "route", ""),
                "metric": getattr(message, "metric", ""),
                "players": getattr(message, "players", []),
            }
            for message in chronological_messages[-6:]
        ]
        memory_context.extend(semantic_memories[:3])

        lane_match = self._classify_lane(rewritten_question)
        allow_groq_classifier = not (
            lane_match != "classifier"
            and _env_enabled("SKIP_GROQ_CLASSIFIER_ON_LANE_MATCH", "1")
        )
        parsed = self.classify_question(
            rewritten_question,
            df,
            available_players,
            default_team=team,
            memory_context=memory_context,
            original_question=question,
            project_id=project_id,
            allow_groq=allow_groq_classifier,
        )
        route = str(parsed.get("route") or "rag")
        query_type = str(parsed.get("type") or "general_pdf_question")
        metric = parsed.get("metric")
        metrics = _as_list(parsed.get("metrics"))
        players = _as_list(parsed.get("players"))
        sources: list[Any] = []
        retrieval_engine: str | None = None

        yield {
            "event": "meta",
            "data": {
                "project_id": project_id,
                "session_id": session_id,
                "original_question": question,
                "rewritten_question": rewritten_question,
            },
        }

        doc_only = (
            str(pdf_scope or "team").strip().lower() == "session"
            and self.store.session_has_index(project_id, session_id)
        )

        answer_parts: list[str] = []

        def _emit(text: str) -> dict[str, Any]:
            answer_parts.append(text)
            return {"event": "token", "data": text}

        if doc_only:
            rag = RagService(
                chunks_csv=self.store.session_chunks_csv(project_id, session_id),
                chroma_dir=self.store.session_chroma_dir(project_id, session_id),
                groq_client=self.groq_client,
            )
            meta: dict[str, Any] = {}
            for piece in rag.answer_stream(rewritten_question, query_type=query_type, meta=meta):
                yield _emit(piece)
            retrieval_engine = meta.get("retrieval_engine")
            sources = meta.get("sources", []) or []
            route = "document_session"
            query_type = "general_pdf_question"
        else:
            hybrid_answer: str | None = None
            hybrid_sources: list[Any] = []
            hybrid_route: str | None = None

            lanes = (
                (self._looks_like_plan_improvement, lambda: self._answer_plan_advice(project_id, rewritten_question, team), "plan_advice"),
                (self._looks_like_schedule, lambda: self._answer_schedule(project_id, rewritten_question), "schedule"),
                (self._looks_like_prediction, lambda: self._answer_predictions(project_id, rewritten_question, team), "prediction"),
                (self._looks_like_match_results, lambda: self._answer_match_results(project_id, rewritten_question), "match_results"),
                (self._looks_like_basketball_stats, lambda: self._answer_basketball_player_stats(project_id, rewritten_question), "basketball_stats"),
                (self._looks_like_lineup_analysis, lambda: self._answer_lineup_analysis(project_id, rewritten_question), "lineup_analysis"),
                (self._looks_like_injuries, lambda: self._answer_injuries(project_id, rewritten_question), "injuries"),
                (self._looks_like_roster, lambda: self._answer_roster(project_id, rewritten_question), "roster"),
                (self._looks_like_profile, lambda: self._answer_profile(project_id, rewritten_question), "profile"),
                (self._looks_like_attendance, lambda: self._answer_attendance(project_id, rewritten_question), "attendance"),
                (self._looks_like_fitness, lambda: self._answer_fitness(project_id, rewritten_question), "fitness"),
                (self._looks_like_coaching_lineups, lambda: self._answer_coaching_lineups(project_id, rewritten_question), "coaching_lineups"),
                (self._looks_like_coach_notes, lambda: self._answer_coach_notes(project_id, rewritten_question), "coach_notes"),
                (self._looks_like_plan, lambda: self._answer_plans(project_id, rewritten_question), "plans"),
                (self._looks_like_report, lambda: self._answer_match_report(project_id, rewritten_question), "match_report"),
            )
            for detector, handler, lane_route in lanes:
                if not detector(rewritten_question):
                    continue
                result = handler()
                if result is not None:
                    hybrid_answer, hybrid_sources = result
                    hybrid_route = lane_route
                    break

            if hybrid_answer is not None:
                sources, route, query_type = hybrid_sources, hybrid_route, hybrid_route
                yield _emit(hybrid_answer)
            elif route == "unsupported":
                yield _emit(_unsupported_answer(parsed))
            elif route == "clarification":
                yield _emit(_clarification_answer(parsed))
            elif route == "analytics":
                analytics_answer, sources = self._answer_analytics(parsed, df, project_id, rewritten_question, team)
                yield _emit(analytics_answer)
            else:
                rag = RagService(
                    chunks_csv=self.store.chunks_csv(project_id),
                    chroma_dir=self.store.chroma_pdf_index_dir(project_id),
                    groq_client=self.groq_client,
                )
                meta = {}
                for piece in rag.answer_stream(rewritten_question, query_type=query_type, meta=meta):
                    yield _emit(piece)
                retrieval_engine = meta.get("retrieval_engine")
                sources = meta.get("sources", []) or []

        answer = "".join(answer_parts)
        memory_store.save_message(session_id, "user", question, parsed, route, str(metric or ""), players)
        memory_store.save_message(session_id, "assistant", answer, parsed, route, str(metric or ""), players)

        yield {
            "event": "done",
            "data": {
                "project_id": project_id,
                "session_id": session_id,
                "answer": answer,
                "type": query_type,
                "route": route,
                "metric": metric,
                "metrics": metrics,
                "players": players,
                "sources": sources,
                "original_question": question,
                "rewritten_question": rewritten_question,
                "retrieval_engine": retrieval_engine,
                "classification_source": parsed.get("_classification_source", "fallback"),
                "team": parsed.get("team"),
                "top_n": parsed.get("top_n"),
                "strategy": parsed.get("strategy"),
                "analytics_recipe": parsed.get("analytics_recipe"),
            },
        }

    def _history_store(self, project_id: str) -> MemoryStore:
        """A read/write history store for `project_id` with Chroma disabled (no
        embedding needed for listing, reading, recording markers, or clearing)."""
        return MemoryStore(
            db_path=self.store.chat_db_path(project_id),
            chroma_dir=self.store.chroma_chat_memory_dir(project_id),
            enable_chroma=False,
            project_id=project_id,
        )

    def list_sessions(self, project_id: str, limit: int = 100) -> list[dict[str, Any]]:
        self.store.ensure_project(project_id)
        return self._history_store(project_id).list_sessions(limit)

    def get_transcript(self, project_id: str, session_id: str) -> list[dict[str, Any]]:
        self.store.ensure_project(project_id)
        messages = self._history_store(project_id).load_messages(session_id)
        return [
            {
                "role": message.role,
                "content": message.content,
                "route": message.route,
                "metric": message.metric,
                "players": message.players,
                "timestamp": message.timestamp,
            }
            for message in messages
        ]

    def clear_session(self, project_id: str, session_id: str) -> None:
        self.store.ensure_project(project_id)
        self._history_store(project_id).clear_session(session_id)

    def record_system_message(self, project_id: str, session_id: str, content: str) -> None:
        """Append a system/assistant marker to a session transcript (e.g. an in-chat
        PDF upload) so the readable history reflects out-of-band events. Best-effort."""
        try:
            self._history_store(project_id).save_message(session_id, "system", content)
        except Exception:
            return

    def classify_question(
        self,
        question: str,
        df: pd.DataFrame,
        available_players: list[str],
        default_team: str = "EGY",
        memory_context: list[dict[str, Any]] | None = None,
        original_question: str | None = None,
        project_id: str = "",
        allow_groq: bool = True,
    ) -> dict[str, Any]:
        deterministic = parse_question(question, available_players, default_team=default_team)
        clarification = self._comparison_clarification(question, deterministic, available_players, default_team)
        if clarification:
            return clarification

        # Phase 2b: when a deterministic hybrid lane already claimed this question
        # upstream, the Groq classifier's output is discarded (the lane produces the
        # answer), so skip the round trip entirely and return the local parse.
        if not allow_groq:
            deterministic["_classification_source"] = "lane_match_skip_groq"
            return deterministic

        if _env_enabled("FAST_ANALYTICS_BYPASS", "0") and deterministic.get("route") == "analytics":
            deterministic["_classification_source"] = "deterministic_fallback"
            return deterministic

        if not self.groq_client.is_configured():
            deterministic["_classification_source"] = "deterministic_fallback"
            return deterministic

        project_context = ProjectContext(
            project_id=project_id,
            available_teams=_available_teams_from_df(df, default_team),
            original_question=original_question,
            groq_client=self.groq_client,
        )
        return classify_question_with_groq_first(
            question=question,
            project_context=project_context,
            available_players=available_players,
            default_team=default_team,
            memory_context=memory_context,
            available_columns=_available_columns_from_df(df),
        )

    def _classify_question_with_groq_first(
        self,
        question: str,
        project_context: ProjectContext,
        available_players: list[str],
        default_team: str,
        memory_context: list[dict[str, Any]] | None = None,
        available_columns: list[str] | None = None,
    ) -> dict[str, Any]:
        deterministic = parse_question(question, available_players, default_team=default_team)
        parsed = self._plan_question_with_groq(
            question=question,
            project_context=project_context,
            available_players=available_players,
            available_columns=available_columns or [],
            default_team=default_team,
            memory_context=memory_context,
        )
        if parsed is None:
            deterministic["_classification_source"] = "deterministic_fallback"
            deterministic["fallback_reason"] = "Groq planner failed or returned invalid JSON."
            return deterministic


        normalized = self._normalize_groq_intent(parsed, question, deterministic, available_players, default_team)
        clarification = self._comparison_clarification(question, normalized, available_players, default_team)
        return clarification or normalized

    def _plan_question_with_groq(
        self,
        question: str,
        project_context: ProjectContext,
        available_players: list[str],
        available_columns: list[str],
        default_team: str,
        memory_context: list[dict[str, Any]] | None = None,
    ) -> dict[str, Any] | None:
        try:
            return self.groq_client.generate_json(
                self._planning_prompt(
                    question=question,
                    project_context=project_context,
                    available_players=available_players,
                    available_columns=available_columns,
                    default_team=default_team,
                    memory_context=memory_context,
                ),
                system=(
                    "You are a basketball analytics planner, not an answer generator. "
                    "You must convert the user's natural coach question into a structured plan. "
                    "Do not answer the question. Do not calculate statistics. "
                    "Only choose the correct route, intent, metrics, players, and analytics recipe. "
                    "Use only the available columns and report types. "
                    "If the requested concept requires unavailable data, return unsupported. "
                    "If the question is ambiguous, return clarification."
                ),
                temperature=0,
                max_tokens=700,
            )
        except Exception:
            return None

    def _comparison_clarification(
        self,
        question: str,
        parsed: dict[str, Any],
        available_players: list[str],
        default_team: str,
    ) -> dict[str, Any] | None:
        metric = detect_stat_metric(question) or parsed.get("metric")
        players = (
            _as_list(parsed.get("players"))
            or _as_list(parsed.get("matched_players"))
            or extract_player_names(question, available_players)
        )
        looks_like_comparison = (
            has_any_pattern(question, COMPARISON_PATTERNS)
            or parsed.get("type") in {"player_comparison", "player_comparison_needs_clarification"}
        )
        if not metric or not looks_like_comparison or len(players) >= 2:
            return None
        missing = _as_list(parsed.get("unmatched_phrases")) or _as_list(parsed.get("missing_players"))
        suggestions = parsed.get("suggestions") if isinstance(parsed.get("suggestions"), dict) else {}
        if not missing:
            missing, suggestions = _missing_comparison_names(question, players, available_players)
        if not missing:
            return None
        return {
            "type": "player_comparison_needs_clarification",
            "route": "clarification",
            "metric": metric,
            "players": players,
            "team": parsed.get("team") or default_team,
            "top_n": None,
            "missing_players": missing,
            "suggestions": suggestions,
            "_classification_source": "clarification",
        }

    def _normalize_groq_intent(
        self,
        intent: dict[str, Any],
        question: str,
        deterministic: dict[str, Any],
        available_players: list[str],
        default_team: str,
    ) -> dict[str, Any]:
        intent_type = _normalise_type(intent.get("type"))
        route = _normalise_route(intent.get("route"))
        if not intent_type or not route:
            deterministic["_classification_source"] = "deterministic_fallback"
            deterministic["fallback_reason"] = "Groq plan had invalid route or type."
            return deterministic

        team = intent.get("team") or deterministic.get("team") or default_team
        metric = _normalise_metric(intent.get("metric"), question, deterministic)
        metrics = _normalise_metric_list(intent.get("metrics"), question, deterministic)
        if metric and metric not in metrics:
            metrics.insert(0, metric)
        players = _match_players(_as_list(intent.get("players")) or _as_list(intent.get("matched_players")), available_players)
        recipe = _normalise_analytics_recipe(intent, intent_type, metric, metrics)
        if not metrics:
            metrics = _normalise_metric_list(recipe.get("metrics"), question, deterministic)
            if metrics:
                recipe["metrics"] = metrics

        if is_player_opportunity_question(question):
            recipe = {
                "name": "opportunity_score",
                "ranking_metric": "opportunity_score",
                "metrics": ["efficiency", "points", "rebounds", "assists", "steals", "blocks", "minutes_seconds"],
                "sort": "desc",
                "filters": {},
                "explanation": "Recommend productive players relative to current playing time.",
            }
            return {
                "type": "analytic_recommendation",
                "route": "analytics",
                "metric": None,
                "metrics": recipe["metrics"],
                "analytics_recipe": recipe,
                "players": [],
                "team": team,
                "top_n": _int_or_default(intent.get("top_n"), extract_top_n(question, default=3)),
                "strategy": str(intent.get("strategy") or "underused_high_impact"),
                "_classification_source": "groq_first",
                "_groq_plan": intent,
                "reason": intent.get("reason"),
            }

        if route == "clarification" and intent_type != "player_comparison_needs_clarification":
            return {
                "type": "clarification",
                "route": "clarification",
                "metric": metric,
                "metrics": metrics,
                "players": players,
                "team": team,
                "top_n": intent.get("top_n"),
                "analytics_recipe": recipe,
                "clarification_question": intent.get("clarification_question")
                or intent.get("reason")
                or "Can you clarify which metric you want?",
                "reason": intent.get("reason"),
                "_classification_source": "groq_first",
                "_groq_plan": intent,
            }

        if intent_type == "player_comparison_needs_clarification":
            suggestions = intent.get("suggestions") if isinstance(intent.get("suggestions"), dict) else {}
            missing = _as_list(intent.get("unmatched_phrases")) or _as_list(intent.get("missing_players"))
            if not missing:
                missing, suggestions = _missing_comparison_names(question, players, available_players)
            return {
                "type": "player_comparison_needs_clarification",
                "route": "clarification",
                "metric": metric or deterministic.get("metric"),
                "metrics": metrics,
                "players": players,
                "team": team,
                "top_n": None,
                "analytics_recipe": recipe,
                "missing_players": missing,
                "suggestions": suggestions,
                "clarification_question": intent.get("clarification_question"),
                "reason": intent.get("reason"),
                "_classification_source": "clarification",
                "_groq_plan": intent,
            }

        if route == "unsupported" or intent_type == "unsupported" or (route == "analytics" and recipe.get("name") == "unsupported"):
            return {
                "type": "unsupported",
                "route": "unsupported",
                "metric": None,
                "metrics": metrics,
                "players": players,
                "team": team,
                "top_n": None,
                "analytics_recipe": recipe,
                "missing_data": intent.get("missing_data") or recipe.get("missing_data"),
                "reason": intent.get("reason") or recipe.get("explanation"),
                "_classification_source": "groq_first",
                "_groq_plan": intent,
            }

        analytics_types = {
            "stat_query",
            "player_comparison",
            "player_summary",
            "player_opportunity_recommendation",
            "analytic_recommendation",
            "squad_recommendation",
        }
        rag_types = {"general_pdf_question", "play_by_play_question", "lineup_question", "plus_minus_question"}
        if intent_type in analytics_types and route != "analytics":
            deterministic["_classification_source"] = "deterministic_fallback"
            return deterministic
        if intent_type in rag_types and route != "rag":
            deterministic["_classification_source"] = "deterministic_fallback"
            return deterministic

        if intent_type == "stat_query":
            if not metric:
                deterministic["_classification_source"] = "deterministic_fallback"
                return deterministic
            base = parse_stat_question(question, default_team=str(team or default_team))
            base.update(
                {
                    "metric": metric,
                    "metrics": metrics or ([metric] if metric else []),
                    "analytics_recipe": recipe,
                    "team": team,
                    "top_n": _int_or_default(intent.get("top_n"), _int_or_default(base.get("top_n"), 3)),
                    "aggregation": str(intent.get("aggregation") or base.get("aggregation") or "sum"),
                    "group_by": str(intent.get("group_by") or base.get("group_by") or "player"),
                    "min_attempts": _nonnegative_int(intent.get("min_attempts"), _nonnegative_int(base.get("min_attempts"), 0)),
                    "players": players or deterministic.get("players", []),
                    "_classification_source": "groq_first",
                    "_groq_plan": intent,
                    "reason": intent.get("reason"),
                }
            )
            return base

        if intent_type == "player_comparison":
            return {
                "type": "player_comparison",
                "route": "analytics",
                "metric": metric or "points",
                "metrics": metrics or ([metric] if metric else ["points"]),
                "analytics_recipe": recipe,
                "players": players,
                "team": team,
                "top_n": None,
                "aggregation": str(intent.get("aggregation") or "sum"),
                "group_by": str(intent.get("group_by") or "player"),
                "min_attempts": _nonnegative_int(intent.get("min_attempts"), 0),
                "_classification_source": "groq_first",
                "_groq_plan": intent,
                "reason": intent.get("reason"),
            }

        if intent_type == "player_summary":
            players = players or deterministic.get("players") or []
            return {
                "type": "player_summary",
                "route": "analytics",
                "metric": metric,
                "metrics": metrics,
                "analytics_recipe": recipe,
                "players": players[:1],
                "team": team,
                "top_n": None,
                "_classification_source": "groq_first",
                "_groq_plan": intent,
                "reason": intent.get("reason"),
            }

        if intent_type in {"player_opportunity_recommendation", "analytic_recommendation"}:
            recipe_metric = str(recipe.get("ranking_metric") or metric or "")
            output_metric = None if recipe.get("name") == "opportunity_score" else (recipe_metric if recipe_metric in SUPPORTED_METRIC_NAMES else metric)
            return {
                "type": "analytic_recommendation",
                "route": "analytics",
                "metric": output_metric,
                "metrics": metrics or _as_list(recipe.get("metrics")) or ([output_metric] if output_metric else []),
                "analytics_recipe": recipe,
                "players": [],
                "team": team,
                "top_n": _int_or_default(intent.get("top_n"), extract_top_n(question, default=3)),
                "strategy": str(intent.get("strategy") or "underused_high_impact"),
                "_classification_source": "groq_first",
                "_groq_plan": intent,
                "reason": intent.get("reason"),
            }

        if intent_type == "squad_recommendation":
            return {
                "type": "squad_recommendation",
                "route": "analytics",
                "metric": None,
                "metrics": metrics,
                "analytics_recipe": recipe,
                "players": [],
                "team": team,
                "top_n": _int_or_default(intent.get("top_n"), extract_top_n(question, default=5)),
                "strategy": str(intent.get("strategy") or detect_squad_strategy(question) or "balanced"),
                "_classification_source": "groq_first",
                "_groq_plan": intent,
                "reason": intent.get("reason"),
            }

        if route != "rag":
            deterministic["_classification_source"] = "deterministic_fallback"
            return deterministic

        return {
            "type": intent_type,
            "route": "rag",
            "metric": None,
            "metrics": metrics,
            "analytics_recipe": recipe,
            "players": players or deterministic.get("players", []),
            "team": team,
            "opponent": intent.get("opponent") or deterministic.get("opponent"),
            "top_n": None,
            "_classification_source": "groq_first",
            "_groq_plan": intent,
            "reason": intent.get("reason"),
        }

    @staticmethod
    def _looks_like_prediction(question: str) -> bool:
        text = (question or "").lower()
        if any(re.search(pattern, text) for pattern in _PREDICTION_PATTERNS):
            return True
        # Lineup suggestions are prediction-first too (model ranks, availability filters).
        return any(re.search(pattern, text) for pattern in _LINEUP_PATTERNS)

    @staticmethod
    def _looks_like_lineup_request(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(pattern, text) for pattern in _LINEUP_PATTERNS)

    def _prediction_service(self) -> Any | None:
        """
        Lazily build a PredictionService bound to this team's project store. Returns
        None (so callers fall back to analytics/RAG) if the prediction module or its
        deps aren't importable in this deployment.
        """
        cached = getattr(self, "_pred_service_cached", "unset")
        if cached != "unset":
            return cached
        service: Any | None = None
        try:
            try:
                from .prediction_service import PredictionService  # type: ignore
            except Exception:
                from prediction_service import PredictionService  # type: ignore
            service = PredictionService(store=self.store)
        except Exception:
            service = None
        self._pred_service_cached = service
        return service

    def _answer_predictions(
        self,
        project_id: str,
        question: str,
        default_team: str,
    ) -> tuple[str, list[Any]] | None:
        """
        Answer from the per-team prediction model's holdout table. Returns None if no
        trained model / predictions exist yet, so the caller can fall through to the
        normal analytics/RAG routing instead of dead-ending on a prediction phrasing.

        Phase 9.4: the model ranks; the live DB filters. If the app integration is
        configured we pull current availability and drop unavailable/injured players
        from the projection so we never suggest someone who can't play. If the DB is
        off or unreachable we degrade to model-only (unavailable=None) and say so.
        """
        lineup = self._looks_like_lineup_request(question)

        service = self._prediction_service()
        predictions = None
        if service is not None:
            try:
                predictions = service.load_predictions(project_id)
            except Exception:
                predictions = None

        unavailable: set[str] | None = None
        client = self._app_data_client()
        if client is not None:
            try:
                unavailable = client.get_unavailable_names(project_id)
            except Exception:
                unavailable = None

        # No trained model / empty holdout: for a lineup ask, fall back to ranking the
        # team's actual box scores (PDF ∪ DB) so we still suggest a five instead of
        # dead-ending. For non-lineup predictive phrasings, defer to analytics/RAG.
        if predictions is None or predictions.empty:
            if lineup:
                fallback = self._lineup_from_box_scores(project_id, question, default_team, unavailable)
                if fallback is not None:
                    return fallback, []
            return None

        return self._format_predictions(predictions, question, unavailable=unavailable, lineup=lineup), []

    def _lineup_from_box_scores(
        self,
        project_id: str,
        question: str,
        default_team: str,
        unavailable: set[str] | None,
    ) -> str | None:
        """
        Suggest a starting five from real box scores when no prediction model exists.
        Ranks the team's players by average efficiency over their logged games (PDF ∪
        DB), drops anyone currently unavailable/injured, and returns a formatted
        lineup. Returns None if there are no box-score rows at all (caller falls
        through to RAG).
        """
        df = self._project_box_scores(project_id, default_team)
        if df is None or df.empty or "player_name" not in df.columns:
            return None
        scoped = df
        if default_team and "team" in scoped.columns:
            team_rows = scoped[scoped["team"].astype(str).str.upper() == default_team.upper()]
            if not team_rows.empty:
                scoped = team_rows
        if "efficiency" not in scoped.columns:
            return None

        ranked = (
            scoped[["player_name", "efficiency"]]
            .dropna(subset=["player_name"])
            .groupby("player_name", as_index=False)["efficiency"]
            .mean()
            .sort_values("efficiency", ascending=False)
        )
        if ranked.empty:
            return None

        top_n = extract_top_n(question, default=5)
        excluded: list[str] = []
        if unavailable:
            def _is_out(name: Any) -> bool:
                return str(name).strip().lower() in unavailable
            excluded = [str(n) for n in ranked["player_name"] if _is_out(n)]
            ranked = ranked[~ranked["player_name"].map(_is_out)]
        if ranked.empty:
            return (
                "Every player with recorded box scores is currently marked unavailable "
                "or injured, so I can't suggest a lineup right now."
            )

        ranked = ranked.head(top_n)
        lines = [f"Suggested starting lineup from recorded box scores (top {len(ranked)} by efficiency):", ""]
        for rank, (_, row) in enumerate(ranked.iterrows(), start=1):
            try:
                value = f"{float(row['efficiency']):.1f}"
            except (TypeError, ValueError):
                value = str(row["efficiency"])
            lines.append(f"{rank}. {row['player_name']} — average efficiency {value}")
        lines.append("")
        if unavailable is None:
            lines.append(
                "Ranked from this team's accumulated box scores. No trained prediction "
                "model is available yet, and live availability couldn't be checked, so no "
                "injury filter was applied."
            )
        elif excluded:
            lines.append(f"Excluded as currently unavailable/injured: {', '.join(excluded)}.")
            lines.append("Ranked from accumulated box scores (no prediction model yet), filtered by current availability.")
        else:
            lines.append("All listed players are currently available. Ranked from accumulated box scores (no prediction model yet).")
        return "\n".join(lines)

    @staticmethod
    def _format_predictions(
        predictions: pd.DataFrame,
        question: str,
        unavailable: set[str] | None = None,
        lineup: bool = False,
    ) -> str:
        name_col = next(
            (col for col in ("Name", "name", "player", "Player", "player_name") if col in predictions.columns),
            None,
        )
        pred_col = next(
            (col for col in ("predicted_EF", "predicted_ef", "prediction", "predicted", "pred_EF") if col in predictions.columns),
            None,
        )
        if name_col is None or pred_col is None:
            return (
                "I have a trained prediction model for this team, but its output table "
                "wasn't in the expected format, so I can't read the projected efficiencies."
            )
        top_n = extract_top_n(question, default=5)
        # The holdout table has one row per player per test match, so a player can
        # repeat. Average each player's projected EF before ranking so the same name
        # doesn't occupy several slots.
        ranked_all = (
            predictions[[name_col, pred_col]]
            .dropna(subset=[pred_col])
            .groupby(name_col, as_index=False)[pred_col]
            .mean()
            .sort_values(pred_col, ascending=False)
        )
        if ranked_all.empty:
            return "The prediction model ran but hasn't produced usable player projections yet."

        # Filter by current availability when the live DB gave us a definite answer.
        excluded: list[str] = []
        if unavailable:
            def _is_out(name: Any) -> bool:
                return str(name).strip().lower() in unavailable
            excluded = [str(row[name_col]) for _, row in ranked_all.iterrows() if _is_out(row[name_col])]
            ranked_all = ranked_all[~ranked_all[name_col].map(_is_out)]

        ranked = ranked_all.head(top_n)
        if ranked.empty:
            return (
                "Every projected player is currently marked unavailable or injured, so I "
                "can't suggest a lineup from the model right now."
            )

        header = (
            f"Suggested starting lineup from the model (top {len(ranked)} available):"
            if lineup
            else f"Model-projected player efficiency for the upcoming match (top {len(ranked)}):"
        )
        lines = [header, ""]
        for rank, (_, row) in enumerate(ranked.iterrows(), start=1):
            try:
                value = f"{float(row[pred_col]):.1f}"
            except (TypeError, ValueError):
                value = str(row[pred_col])
            lines.append(f"{rank}. {row[name_col]} — predicted efficiency {value}")
        lines.append("")
        if unavailable is None:
            lines.append(
                "These are model projections from the team's accumulated match data, not "
                "box-score totals. (Live availability couldn't be checked, so no injury filter was applied.)"
            )
        elif excluded:
            lines.append(f"Excluded as currently unavailable/injured: {', '.join(excluded)}.")
            lines.append("Projections come from accumulated match data, filtered by current availability.")
        else:
            lines.append("All projected players are currently available. Projections come from accumulated match data.")
        return "\n".join(lines)

    @staticmethod
    def _looks_like_roster(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(pattern, text) for pattern in _ROSTER_PATTERNS)

    def _app_data_client(self) -> Any | None:
        cached = getattr(self, "_app_client_cached", "unset")
        if cached != "unset":
            return cached
        client: Any | None = None
        try:
            try:
                from .app_data_client import AppDataClient  # type: ignore
            except Exception:
                from app_data_client import AppDataClient  # type: ignore
            candidate = AppDataClient()
            client = candidate if candidate.is_configured() else None
        except Exception:
            client = None
        self._app_client_cached = client
        return client

    def _project_team_code(self, project_id: str, fallback: str | None = None) -> str:
        """Resolve the box-score team code for a project (Phase 1).

        Prefers the authoritative code from the app (AppDataClient.get_team); falls
        back to the caller-supplied team, then to a last-resort default. Cached per
        project so repeated lanes in one request don't re-hit the app. This is what
        removes the hardcoded "EGY" from the DB-adapter path for non-Egypt projects.
        """
        cache = getattr(self, "_team_code_cache", None)
        if cache is None:
            cache = {}
            self._team_code_cache = cache
        if project_id in cache:
            return cache[project_id] or (fallback or "EGY")

        code: str | None = None
        client = self._app_data_client()
        if client is not None and hasattr(client, "get_team"):
            try:
                info = client.get_team(project_id)
            except Exception:
                info = None
            if isinstance(info, dict):
                code = str(info.get("code") or "").strip() or None
        cache[project_id] = code
        return code or (fallback or "EGY")

    def _db_box_scores(self, project_id: str, team: str, *, unified: bool) -> pd.DataFrame:
        """DB-derived box-score frame via the adapter.

        When ``unified`` is True, reads the deduplicated .NET unified view
        (get_unified_box_scores); otherwise the raw per-game rows
        (get_match_player_stats). Returns an empty frame when the app is off /
        unreachable / the endpoint is absent, so callers fall back to the CSV.
        """
        client = self._app_data_client()
        if client is None:
            return pd.DataFrame()
        rows = None
        try:
            if unified:
                getter = getattr(client, "get_unified_box_scores", None)
                rows = getter(project_id) if getter is not None else None
            else:
                rows = client.get_match_player_stats(project_id)
        except Exception:
            rows = None
        return box_scores_from_match_player_stats(rows, team=team)

    def _spawn_box_scores(self, project_id: str, team: str | None):
        """Phase 2d: start the box-score load on a worker thread so it overlaps the
        caller's memory/rewrite work. Returns (executor, future); the caller resolves
        the future and shuts the executor down. The work is fully fail-soft inside
        _project_box_scores, so the future returns a frame (possibly empty), never an
        error — but the caller still guards .result() defensively."""
        executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="boxscores")
        future = executor.submit(self._project_box_scores, project_id, team)
        return executor, future

    def _project_box_scores(self, project_id: str, team: str | None = None) -> pd.DataFrame:
        """Box-score frame for a team, cached per data version (Phase 2.5f).

        Thin caching wrapper over _build_box_scores: the assembled frame is reused
        within a TTL window and a team version stamp (so an ingest bump rebuilds it),
        instead of re-running the source ladder + dedup/enrich on every request. The
        returned frame is shared and must be treated as read-only by callers.
        """
        source = os.getenv("BOX_SCORE_SOURCE", "unified").strip().lower()
        team_code = self._project_team_code(project_id, team)
        csv_mtime = _path_mtime(self.store.box_score_csv(project_id))
        version = get_team_version(project_id)
        cache_key = f"{project_id}|{team_code}|{source}|{team or ''}|{csv_mtime}"
        cached = _FRAME_CACHE.get(cache_key, version)
        if cached is not _MISS:
            return cached
        frame = self._build_box_scores(project_id, team, source, team_code)
        _FRAME_CACHE.set(cache_key, version, frame)
        return frame

    def _build_box_scores(
        self, project_id: str, team: str | None, source: str, team_code: str
    ) -> pd.DataFrame:
        """
        Box-score DataFrame for a team's analytics lanes (Phase 1).

        Source order is controlled by BOX_SCORE_SOURCE:

          * ``unified`` (default) — read the deduplicated .NET unified-box-scores view
            (PDF-vs-DB overlap already resolved in SQL, so NO Python dedup). When that
            endpoint isn't deployed it returns nothing and we fall back to the
            ``csv_first`` ladder — identical to the previous behavior, so nothing
            changes until the view ships, then it upgrades automatically.
          * ``db_first`` — live DB wins, CSV fallback.
          * ``csv_first`` — PDF-derived CSV wins, DB fallback (the Phase 0 behavior).
          * ``union`` — best-effort PDF ∪ DB merge via box_scores_union (fallback only;
            the authoritative dedup belongs in SQL, not here).

        Degrades silently to whatever data exists if the DB is off/unreachable.
        ``source`` and ``team_code`` are resolved by the _project_box_scores cache
        wrapper and passed in so the cache key and the build agree.
        """
        csv_df = load_project_box_scores(self.store.box_score_csv(project_id))

        def _has_team(frame: pd.DataFrame) -> bool:
            if frame is None or frame.empty:
                return False
            if not team or "team" not in frame.columns:
                return True
            return bool((frame["team"].astype(str).str.upper() == team.upper()).any())

        # Authoritative path: deduplicated unified view. Non-empty result wins outright.
        if source == "unified":
            unified_df = self._db_box_scores(project_id, team_code, unified=True)
            if not unified_df.empty and _has_team(unified_df):
                return unified_df
            # Endpoint absent/empty -> behave exactly like csv_first (Phase 0).
            source = "csv_first"

        if source == "union":
            db_df = self._db_box_scores(project_id, team_code, unified=False)
            merged = box_scores_union(csv_df, db_df, source_priority="db")
            return merged if not merged.empty else csv_df

        if source == "db_first":
            db_df = self._db_box_scores(project_id, team_code, unified=False)
            if not db_df.empty and _has_team(db_df):
                return db_df
            if _has_team(csv_df):
                return csv_df
            return db_df if not db_df.empty else csv_df

        # csv_first (default fallback): CSV preferred, DB fills the gap.
        if _has_team(csv_df):
            return csv_df
        db_df = self._db_box_scores(project_id, team_code, unified=False)
        if db_df.empty:
            return csv_df
        if csv_df is None or csv_df.empty:
            return db_df
        # CSV had data but not this team: union so neither plane is lost (dedup-safe).
        return box_scores_union(csv_df, db_df, source_priority="csv")

    def _answer_roster(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        """
        Answer roster / injury / availability questions from the app's live roster.
        Returns None if the app integration is off or the call fails, so the caller
        falls through to RAG instead of dead-ending.
        """
        client = self._app_data_client()
        if client is None:
            return None
        roster = client.get_roster(project_id)
        if not roster:
            return None
        return self._format_roster(roster, question), []

    @staticmethod
    def _format_roster(roster: list[dict[str, Any]], question: str) -> str:
        injuries_only = bool(re.search(r"\binjur(?:y|ies|ed)\b|\bsidelined\b|\bwho\s+is\s+out\b|\bwho'?s\s+out\b", (question or "").lower()))

        def _label(member: dict[str, Any]) -> str:
            name = str(member.get("name") or "Unknown")
            jersey = member.get("jersey_number")
            position = member.get("position")
            bits = [name]
            if jersey not in (None, ""):
                bits.append(f"#{jersey}")
            if position:
                bits.append(str(position))
            return " ".join(bits)

        injured = [m for m in roster if m.get("is_injured")]

        if injuries_only:
            if not injured:
                return "No players are currently flagged as injured on this team's roster."
            lines = ["Currently injured (uncleared):", ""]
            for member in injured:
                injury = str(member.get("injury_type") or "injury")
                lines.append(f"- {_label(member)} — {injury}")
            return "\n".join(lines)

        players = [m for m in roster if str(m.get("role", "")).lower() == "player"] or roster
        lines = [f"Current roster ({len(players)} players):", ""]
        for member in players:
            suffix = ""
            if member.get("is_injured"):
                suffix = f" — injured ({member.get('injury_type') or 'uncleared'})"
            lines.append(f"- {_label(member)}{suffix}")
        if injured:
            lines.append("")
            lines.append(f"{len(injured)} currently injured.")
        return "\n".join(lines)

    # ---------------------------------------------------------------------
    # Injury-detail lane: per-injury diagnosis / expected return / recovery
    # tips, and "most injured" ranking. Richer than the roster injured list.
    # ---------------------------------------------------------------------

    @staticmethod
    def _looks_like_injuries(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _INJURY_PATTERNS)

    def _answer_injuries(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        """
        Answer injury-detail questions from the app's active (uncleared) medical
        records: diagnosis, expected return date, and recovery tips per injury, plus
        a "most injured" ranking. Returns None if the integration is off or the call
        fails so the caller falls through to the roster lane (then RAG).
        """
        client = self._app_data_client()
        if client is None:
            return None
        injuries = client.get_injuries(project_id)
        if injuries is None:
            return None
        if not injuries:
            return "No players have active (uncleared) injuries on this team right now.", []

        text = (question or "").lower()

        # "most injured" — rank players by number of active injury records.
        if re.search(r"\bmost\s+injured\b", text) or re.search(r"\bmost\s+injur(?:y|ies)\b", text):
            counts: dict[str, int] = {}
            for rec in injuries:
                name = str(rec.get("name") or "Unknown")
                counts[name] = counts.get(name, 0) + 1
            ranked = sorted(counts.items(), key=lambda kv: kv[1], reverse=True)
            top_name, top_count = ranked[0]
            lines = [
                f"Most injured: {top_name} — {top_count} active injury record(s).",
                "",
                "Active injury counts:",
            ]
            for name, count in ranked:
                lines.append(f"- {name}: {count}")
            return self._phrase_db_answer(question, "\n".join(lines)), []

        # Otherwise: detailed per-injury list.
        lines = [f"Active injuries ({len(injuries)}):", ""]
        for rec in injuries:
            name = str(rec.get("name") or "Unknown")
            itype = str(rec.get("injury_type") or "injury")
            bits = [f"- {name} — {itype}"]
            diagnosis = str(rec.get("diagnosis") or "").strip()
            if diagnosis:
                bits.append(f"diagnosis: {diagnosis}")
            ret = str(rec.get("expected_return_date") or "").strip()
            if ret:
                bits.append(f"expected return: {ret}")
            tips = str(rec.get("recovery_tips") or "").strip()
            if tips:
                bits.append(f"recovery: {tips}")
            lines.append("; ".join(bits))
        return self._phrase_db_answer(question, "\n".join(lines)), []

    # ---------------------------------------------------------------------
    # Player physical-profile lane: height / weight from the live roster.
    # ---------------------------------------------------------------------

    @staticmethod
    def _looks_like_profile(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _PROFILE_PATTERNS)

    def _answer_profile(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        """
        Answer height/weight questions from the live roster (which now carries height
        and weight per player). Supports tallest/heaviest-style superlatives. Returns
        None if the integration is off / the call fails so we fall through to RAG.
        """
        client = self._app_data_client()
        if client is None:
            return None
        roster = client.get_roster(project_id)
        if not roster:
            return None

        text = (question or "").lower()
        players = [m for m in roster if str(m.get("role", "")).lower() == "player"] or roster

        def _num(member: dict[str, Any], key: str) -> float | None:
            try:
                v = member.get(key)
                return float(v) if v not in (None, "") else None
            except (TypeError, ValueError):
                return None

        # Superlative shortcuts.
        superlatives = (
            ("tallest", "height", True),
            ("shortest", "height", False),
            ("heaviest", "weight", True),
            ("lightest", "weight", False),
        )
        for word, key, want_max in superlatives:
            if word in text:
                rated = [(m, _num(m, key)) for m in players]
                rated = [(m, v) for m, v in rated if v is not None]
                if not rated:
                    return f"No {key} data is recorded for this team's players.", []
                pick = (max if want_max else min)(rated, key=lambda mv: mv[1])
                member, value = pick
                unit = "cm" if key == "height" else "kg"
                return (
                    f"{word.capitalize()} player: {member.get('name') or 'Unknown'} "
                    f"({value:g} {unit})."
                ), []

        # Otherwise: list height/weight for each player.
        lines = ["Player height & weight:", ""]
        any_data = False
        for member in players:
            name = str(member.get("name") or "Unknown")
            h = _num(member, "height")
            w = _num(member, "weight")
            bits: list[str] = []
            if h is not None:
                bits.append(f"{h:g} cm")
            if w is not None:
                bits.append(f"{w:g} kg")
            if bits:
                any_data = True
            detail = ", ".join(bits) if bits else "no measurements recorded"
            lines.append(f"- {name}: {detail}")
        if not any_data:
            return "No height or weight measurements are recorded for this team yet.", []
        return self._phrase_db_answer(question, "\n".join(lines)), []

    # ---------------------------------------------------------------------
    # Pure routing classifier (testable). Mirrors the `lanes` order in ask()
    # exactly, but depends ONLY on the detectors — never on data availability —
    # so the routing decision can be unit-tested in isolation. Returns the lane
    # name of the first detector that matches, or "classifier" when no hybrid
    # lane claims the question (i.e. it falls through to analytics/RAG).
    #
    # NOTE: keep this list byte-for-byte in step with the `lanes` tuple in ask().
    # The routing regression tests assert against the names returned here.
    # ---------------------------------------------------------------------
    @classmethod
    def _classify_lane(cls, question: str) -> str:
        ordered = (
            (cls._looks_like_plan_improvement, "plan_advice"),
            (cls._looks_like_schedule, "schedule"),
            (cls._looks_like_prediction, "prediction"),
            (cls._looks_like_match_results, "match_results"),
            (cls._looks_like_basketball_stats, "basketball_stats"),
            (cls._looks_like_lineup_analysis, "lineup_analysis"),
            (cls._looks_like_injuries, "injuries"),
            (cls._looks_like_roster, "roster"),
            (cls._looks_like_profile, "profile"),
            (cls._looks_like_attendance, "attendance"),
            (cls._looks_like_fitness, "fitness"),
            (cls._looks_like_coaching_lineups, "coaching_lineups"),
            (cls._looks_like_coach_notes, "coach_notes"),
            (cls._looks_like_plan, "plans"),
            (cls._looks_like_report, "match_report"),
        )
        for detector, lane_route in ordered:
            if detector(question):
                return lane_route
        return "classifier"

    # ---------------------------------------------------------------------
    # Hybrid live-DB lanes (Phase 9): schedule, attendance, fitness, plans,
    # plan advisory. All read-only, all no-ops unless the app API is configured.
    # ---------------------------------------------------------------------

    @staticmethod
    def _looks_like_schedule(question: str) -> bool:
        text = (question or "").lower()
        # Defer to the prediction lane when a prediction OR lineup-request phrasing is
        # present, so the broad schedule terms ("next game", "this week", "events")
        # never hijack a model question. Lineup requests ("recommend a lineup for the
        # next game") are prediction-first, so they must not be captured here even
        # though they contain a schedule term.
        if any(re.search(p, text) for p in _PREDICTION_PATTERNS):
            return False
        if any(re.search(p, text) for p in _LINEUP_PATTERNS):
            return False
        return any(re.search(p, text) for p in _SCHEDULE_PATTERNS)

    @staticmethod
    def _looks_like_attendance(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _ATTENDANCE_PATTERNS)

    @staticmethod
    def _looks_like_fitness(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _FITNESS_PATTERNS)

    @staticmethod
    def _looks_like_plan(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _PLAN_PATTERNS)

    @staticmethod
    def _looks_like_plan_improvement(question: str) -> bool:
        text = (question or "").lower()
        has_plan = ("plan" in text) or any(re.search(p, text) for p in _PLAN_PATTERNS)
        if not has_plan:
            return False
        return any(re.search(p, text) for p in _PLAN_IMPROVE_PATTERNS)

    @staticmethod
    def _fmt_dt(value: Any) -> str:
        s = str(value or "").strip()
        if not s:
            return "TBD"
        try:
            from datetime import datetime
            dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
            return dt.strftime("%Y-%m-%d %H:%M")
        except Exception:
            return s[:16].replace("T", " ")

    def _phrase_db_answer(self, question: str, draft: str) -> str:
        """
        Optional LLM phrasing pass for live-DB lane answers (Phase 9.7). The coach
        chose LLM-phrased answers, so this defaults ON, but it ALWAYS preserves the
        deterministic draft if Groq is unconfigured or fails — facts are computed in
        Python, Groq only rewords. Disable with ENABLE_HYBRID_LLM_FORMATTING=0.
        """
        if not draft.strip():
            return draft
        if not _env_enabled("ENABLE_HYBRID_LLM_FORMATTING", "1"):
            return draft
        if not self.groq_client.is_configured():
            return draft
        system = (
            "You format live team-data answers for a coach. Do NOT invent, drop, or "
            "change any names, numbers, dates, or facts. Only improve clarity and tone. "
            "Keep it concise."
        )
        prompt = (
            f"Coach question:\n{question}\n\n"
            f"Draft answer (every fact here is correct — keep them all):\n{draft}\n\n"
            "Return a clear, concise answer preserving every fact."
        )
        try:
            out = self.groq_client.generate_text(prompt, system=system, temperature=0.1, max_tokens=600)
            return out if out.strip() else draft
        except Exception:
            return draft

    @staticmethod
    def _parse_event_start(value: Any) -> "datetime | None":
        from datetime import datetime
        s = str(value or "").strip()
        if not s:
            return None
        try:
            dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
        except Exception:
            return None
        # Normalise to naive (drop tz) so comparisons against a naive "now" never raise.
        if dt.tzinfo is not None:
            dt = dt.replace(tzinfo=None)
        return dt

    @staticmethod
    def _schedule_window(question: str) -> "tuple[str, datetime, datetime] | None":
        """
        Map a time-window phrase in the question to a (label, start, end) range.
        Returns None when the question names no explicit window (caller then lists
        the full upcoming schedule). Ranges are inclusive of start, exclusive of end.
        """
        from datetime import datetime, timedelta
        text = (question or "").lower()
        now = datetime.now()
        today = datetime(now.year, now.month, now.day)

        if re.search(r"\btoday\b", text):
            return "today", today, today + timedelta(days=1)
        if re.search(r"\btomorrow\b", text):
            start = today + timedelta(days=1)
            return "tomorrow", start, start + timedelta(days=1)
        if re.search(r"\bthis\s+weekend\b", text) or re.search(r"\bthe\s+weekend\b", text):
            # Saturday (weekday 5) and Sunday of the current week.
            sat = today + timedelta(days=(5 - today.weekday()) % 7)
            return "this weekend", sat, sat + timedelta(days=2)
        if re.search(r"\bthis\s+week\b", text) or re.search(r"\bthe\s+week\b", text):
            # From now through end of Sunday (week starts Monday).
            end = today + timedelta(days=(7 - today.weekday()))
            return "this week", now, end
        if re.search(r"\bnext\s+week\b", text):
            start = today + timedelta(days=(7 - today.weekday()))
            return "next week", start, start + timedelta(days=7)
        if re.search(r"\bthis\s+month\b", text):
            if today.month == 12:
                end = datetime(today.year + 1, 1, 1)
            else:
                end = datetime(today.year, today.month + 1, 1)
            return "this month", now, end
        return None

    def _answer_schedule(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        events = client.get_schedule(project_id)
        if events is None:
            return None
        if not events:
            return "No upcoming events are on this team's schedule.", []

        window = self._schedule_window(question)
        # "next match/event/training" (singular, no window) → just the soonest one.
        text = (question or "").lower()
        wants_single = (
            window is None
            and re.search(r"\bnext\s+(?:event|match|game|fixture|training|practice|session|meeting)\b", text) is not None
        )

        selected = events
        header = "Upcoming schedule:"
        if window is not None:
            label, start, end = window
            filtered = []
            for event in events:
                dt = self._parse_event_start(event.get("start_at"))
                if dt is not None and start <= dt < end:
                    filtered.append(event)
            if not filtered:
                return f"No events are scheduled {label}.", []
            selected = filtered
            header = f"Events {label}:"
        elif wants_single:
            selected = events[:1]
            header = "Next up:"

        lines = [header, ""]
        for event in selected[:25]:
            title = str(event.get("title") or "Event")
            etype = str(event.get("event_type") or "")
            start = self._fmt_dt(event.get("start_at"))
            location = event.get("location")
            bit = f"- {start} — {title}"
            if etype:
                bit += f" ({etype})"
            if location:
                bit += f" @ {location}"
            lines.append(bit)
        return self._phrase_db_answer(question, "\n".join(lines)), []

    def _answer_attendance(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        rows = client.get_attendance(project_id)
        if rows is None:
            return None
        if not rows:
            return "No attendance has been recorded for this team yet.", []
        rows = sorted(rows, key=lambda r: r.get("rate") if isinstance(r.get("rate"), (int, float)) else 1.0)
        lines = ["Attendance (recent window, lowest first):", ""]
        for row in rows[:25]:
            name = str(row.get("name") or "Unknown")
            present = row.get("present") or 0
            total = row.get("total") or 0
            rate = row.get("rate")
            pct = f"{round(float(rate) * 100)}%" if isinstance(rate, (int, float)) else "n/a"
            lines.append(f"- {name}: {present}/{total} ({pct})")
        return self._phrase_db_answer(question, "\n".join(lines)), []

    def _answer_fitness(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        rows = client.get_fitness(project_id)
        if rows is None:
            return None
        if not rows:
            return "No fitness records have been logged for this team yet.", []
        lines = ["Latest fitness readings:", ""]
        for row in rows[:25]:
            name = str(row.get("name") or "Unknown")
            bits: list[str] = []
            if row.get("height") is not None:
                bits.append(f"height {row['height']}")
            if row.get("weight") is not None:
                bits.append(f"weight {row['weight']}")
            if row.get("bmi") is not None:
                bits.append(f"BMI {row['bmi']}")
            if row.get("body_fat_pct") is not None:
                bits.append(f"body fat {row['body_fat_pct']}%")
            if row.get("speed_test_result") is not None:
                bits.append(f"speed {row['speed_test_result']}")
            if row.get("endurance_score") is not None:
                bits.append(f"endurance {row['endurance_score']}")
            if row.get("custom_test_name") and row.get("custom_test_result") is not None:
                bits.append(f"{row['custom_test_name']} {row['custom_test_result']}")
            detail = ", ".join(bits) if bits else "no metrics recorded"
            recorder = str(row.get("recorded_by_name") or "").strip()
            suffix = f" — recorded by {recorder}" if recorder else ""
            lines.append(f"- {name}: {detail}{suffix}")
        return self._phrase_db_answer(question, "\n".join(lines)), []

    def _answer_plans(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        plans = client.get_plans(project_id)
        if plans is None:
            return None
        if not plans:
            return "There are no coaching plans saved for this team yet.", []
        latest = plans[0]
        title = str(latest.get("title") or "Untitled plan")
        description = str(latest.get("description") or "").strip()
        content = str(latest.get("content") or "").strip()
        lines = [f"Current plan: {title}", ""]
        if description:
            lines.extend([description, ""])
        if content:
            lines.append(content)
        if len(plans) > 1:
            others = ", ".join(str(p.get("title") or "Untitled") for p in plans[1:6])
            lines.extend(["", f"Other plans: {others}"])
        return self._phrase_db_answer(question, "\n".join(lines)), []

    # ---------------------------------------------------------------------
    # Basketball box-score lanes (Phase 9.9): per-player stats, match results,
    # lineup on/off analysis, saved coaching lineups, coach notes. All read-only
    # and no-ops unless the app API is configured.
    # ---------------------------------------------------------------------

    @staticmethod
    def _looks_like_basketball_stats(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _BASKETBALL_STAT_PATTERNS)

    @staticmethod
    def _looks_like_match_results(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _MATCH_RESULT_PATTERNS)

    @staticmethod
    def _looks_like_report(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _REPORT_PATTERNS)

    @staticmethod
    def _looks_like_lineup_analysis(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _LINEUP_ANALYSIS_PATTERNS)

    @staticmethod
    def _looks_like_coaching_lineups(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _COACHING_LINEUP_PATTERNS)

    @staticmethod
    def _looks_like_coach_notes(question: str) -> bool:
        text = (question or "").lower()
        return any(re.search(p, text) for p in _COACH_NOTES_PATTERNS)

    @staticmethod
    def _parse_made_attempted(value: Any) -> tuple[int, int]:
        """Parse a "made/attempted" string (e.g. "20/44") into (made, attempted).
        Returns (0, 0) for missing/malformed values."""
        s = str(value or "").strip()
        if "/" not in s:
            return (0, 0)
        left, _, right = s.partition("/")
        try:
            return (int(float(left.strip())), int(float(right.strip())))
        except (ValueError, TypeError):
            return (0, 0)

    @staticmethod
    def _bb_metric_from_question(question: str) -> tuple[str, str, str] | None:
        """Return (label, kind, field) for the first basketball metric whose pattern
        matches the question, or None if no specific metric is named."""
        text = (question or "").lower()
        for label, kind, field, patterns in _BB_METRICS:
            if any(re.search(p, text) for p in patterns):
                return (label, kind, field)
        return None

    @staticmethod
    def _extract_top_n(question: str, default: int = 5) -> int:
        m = re.search(r"\b(?:top|best|leading|first)\s+(\d{1,2})\b", (question or "").lower())
        if m:
            try:
                n = int(m.group(1))
                return max(1, min(n, 25))
            except ValueError:
                pass
        return default

    @staticmethod
    def _find_named_player(question: str, names: list[str]) -> str | None:
        """Return the roster name explicitly referenced in the question (longest
        full-name or surname match wins), or None for a team-wide question."""
        text = (question or "").lower()
        best: str | None = None
        best_len = 0
        for name in names:
            n = str(name or "").strip()
            if not n:
                continue
            candidates = [n.lower()] + [tok for tok in n.lower().split() if len(tok) >= 3]
            for cand in candidates:
                if re.search(rf"\b{re.escape(cand)}\b", text) and len(cand) > best_len:
                    best, best_len = n, len(cand)
        return best

    def _aggregate_bb_player_stats(self, rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
        """Collapse per-game + cumulative rows into one record per player. When a
        cumulative row exists for a player it is used as the source of truth;
        otherwise per-game rows are summed."""
        int_fields = (
            "points", "total_rebounds", "offensive_rebounds", "defensive_rebounds",
            "assists", "steals", "blocks", "turnovers", "efficiency", "personal_fouls",
        )
        ratio_fields = ("two_pt_ma", "three_pt_ma", "ft_ma")

        by_player: dict[str, dict[str, Any]] = {}
        for row in rows:
            key = str(row.get("name") or row.get("player_user_id") or "Unknown")
            entry = by_player.setdefault(key, {"name": str(row.get("name") or "Unknown"),
                                               "cumulative": None, "games": []})
            gran = str(row.get("granularity") or "").lower()
            if "cumulative" in gran:
                entry["cumulative"] = row
            else:
                entry["games"].append(row)

        out: list[dict[str, Any]] = []
        for entry in by_player.values():
            ints: dict[str, int] = {f: 0 for f in int_fields}
            ratios: dict[str, tuple[int, int]] = {f: (0, 0) for f in ratio_fields}
            cum = entry["cumulative"]
            if cum is not None:
                for f in int_fields:
                    v = cum.get(f)
                    ints[f] = int(v) if isinstance(v, (int, float)) else 0
                for f in ratio_fields:
                    ratios[f] = self._parse_made_attempted(cum.get(f))
                games = cum.get("games_played")
                games = int(games) if isinstance(games, (int, float)) else len(entry["games"])
            else:
                for g in entry["games"]:
                    for f in int_fields:
                        v = g.get(f)
                        if isinstance(v, (int, float)):
                            ints[f] += int(v)
                    for f in ratio_fields:
                        m, a = self._parse_made_attempted(g.get(f))
                        pm, pa = ratios[f]
                        ratios[f] = (pm + m, pa + a)
                games = len(entry["games"])
            out.append({"name": entry["name"], "games": games, "int": ints, "ratio": ratios})
        return out

    def _answer_basketball_player_stats(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        rows = client.get_match_player_stats(project_id)
        if rows is None:
            return None
        if not rows:
            return "No basketball box scores have been imported for this team yet.", []

        agg = self._aggregate_bb_player_stats(rows)
        if not agg:
            return "No per-player basketball stats are available for this team yet.", []

        names = [a["name"] for a in agg]
        named = self._find_named_player(question, names)
        metric = self._bb_metric_from_question(question)

        # Specific player → full stat line.
        if named is not None:
            rec = next((a for a in agg if a["name"] == named), None)
            if rec is not None:
                return self._phrase_db_answer(question, self._format_bb_player_line(rec)), []

        # Otherwise rank by the requested metric (default: points).
        label, kind, field = metric if metric is not None else ("points", "int", "points")
        top_n = self._extract_top_n(question, 5)
        wants_pct = bool(re.search(r"\b(?:percent(?:age)?|pct|accuracy|efficient|%|best\s+shoot)\b", question.lower()))

        if kind == "ratio":
            ranked = []
            for a in agg:
                made, att = a["ratio"][field]
                pct = (made / att) if att > 0 else 0.0
                ranked.append((a, made, att, pct))
            if wants_pct:
                ranked = [r for r in ranked if r[2] >= 5]  # min attempts for a meaningful %
                ranked.sort(key=lambda r: r[3], reverse=True)
            else:
                ranked.sort(key=lambda r: r[1], reverse=True)
            ranked = [r for r in ranked if r[2] > 0][:top_n]
            if not ranked:
                return f"No {label} have been recorded for this team yet.", []
            header = f"Top {len(ranked)} by {label}" + (" (by accuracy)" if wants_pct else " (by makes)") + ":"
            lines = [header, ""]
            for i, (a, made, att, pct) in enumerate(ranked, 1):
                lines.append(f"{i}. {a['name']}: {made}/{att} ({round(pct * 100)}%)")
            return self._phrase_db_answer(question, "\n".join(lines)), []

        # Integer metric.
        ranked_i = [a for a in agg if a["int"].get(field, 0) > 0]
        ranked_i.sort(key=lambda a: a["int"].get(field, 0), reverse=True)
        ranked_i = ranked_i[:top_n]
        if not ranked_i:
            return f"No {label} have been recorded for this team yet.", []
        lines = [f"Top {len(ranked_i)} by {label}:", ""]
        for i, a in enumerate(ranked_i, 1):
            total = a["int"].get(field, 0)
            g = a.get("games") or 0
            per = f" ({round(total / g, 1)}/g)" if g else ""
            lines.append(f"{i}. {a['name']}: {total} {label}{per}")
        return self._phrase_db_answer(question, "\n".join(lines)), []

    @staticmethod
    def _format_bb_player_line(rec: dict[str, Any]) -> str:
        ints = rec["int"]
        ratios = rec["ratio"]
        g = rec.get("games") or 0
        parts = [f"{rec['name']} — {g} game(s) recorded:"]
        stat_bits = [
            f"{ints.get('points', 0)} pts",
            f"{ints.get('total_rebounds', 0)} reb",
            f"{ints.get('assists', 0)} ast",
            f"{ints.get('steals', 0)} stl",
            f"{ints.get('blocks', 0)} blk",
            f"{ints.get('turnovers', 0)} TO",
        ]
        parts.append("  " + ", ".join(stat_bits))

        def _shot(label: str, key: str) -> str | None:
            made, att = ratios.get(key, (0, 0))
            if att <= 0:
                return None
            return f"{label} {made}/{att} ({round(made / att * 100)}%)"

        shots = [s for s in (_shot("2PT", "two_pt_ma"), _shot("3PT", "three_pt_ma"), _shot("FT", "ft_ma")) if s]
        if shots:
            parts.append("  Shooting: " + ", ".join(shots))
        if ints.get("efficiency"):
            parts.append(f"  Efficiency: {ints['efficiency']}")
        return "\n".join(parts)

    def _answer_match_results(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        games = client.get_match_team_stats(project_id)
        if games is None:
            return None
        # Per-game rows only (exclude cumulative team totals) for a results list.
        per_game = [g for g in games if "cumulative" not in str(g.get("granularity") or "").lower()]
        if not per_game:
            per_game = games
        if not per_game:
            return "No game results have been imported for this team yet.", []

        wins = sum(1 for g in per_game if str(g.get("result") or "").upper().startswith("W"))
        losses = sum(1 for g in per_game if str(g.get("result") or "").upper().startswith("L"))
        lines = [f"Record: {wins}-{losses} across {len(per_game)} recorded game(s).", ""]
        for g in per_game[:15]:
            opp = str(g.get("opponent_name") or g.get("matchup") or "Opponent")
            ts, os_ = g.get("team_score"), g.get("opponent_score")
            score = f"{ts}-{os_}" if ts is not None and os_ is not None else "score n/a"
            result = str(g.get("result") or "").strip()
            tag = f" ({result})" if result else ""
            comp = str(g.get("competition_name") or "").strip()
            comp_bit = f" — {comp}" if comp else ""
            lines.append(f"- vs {opp}: {score}{tag}{comp_bit}")
        return self._phrase_db_answer(question, "\n".join(lines)), []

    def _answer_match_report(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        """
        Build a written match report from the team's stored match-reports (DB), not
        from whatever PDF is indexed. If the coach named an opponent we only use the
        report(s) for that opponent; if none exist we return None so the caller falls
        through to PDF RAG (whose opponent guard then refuses rather than reporting on
        the wrong game). With no opponent named, summarise the most recent report.
        """
        client = self._app_data_client()
        if client is None:
            return None
        reports = client.get_match_reports(project_id)
        if reports is None:
            return None
        if not reports:
            return None  # no DB reports -> let RAG try the PDFs

        try:
            from .rag_service import _question_opponents  # type: ignore
        except Exception:
            from rag_service import _question_opponents  # type: ignore

        opponents = _question_opponents(question)

        def _matches(report: dict[str, Any]) -> bool:
            opp = str(report.get("opponent_name") or "").lower()
            return any(term in opp for term in opponents)

        if opponents:
            selected = [r for r in reports if _matches(r)]
            if not selected:
                return None  # named an opponent we have no report for -> fall through
        else:
            # No opponent named: most recent report (by match_date when present).
            selected = sorted(
                reports,
                key=lambda r: str(r.get("match_date") or ""),
                reverse=True,
            )[:1]

        blocks: list[str] = []
        for report in selected[:3]:
            opp = str(report.get("opponent_name") or "Opponent").strip()
            date = str(report.get("match_date") or "").strip()
            comp = str(report.get("competition") or "").strip()
            result = str(report.get("result") or "").strip()
            ts, os_ = report.get("team_score"), report.get("opponent_score")
            score = f"{ts}-{os_}" if ts is not None and os_ is not None else ""
            header_bits = [f"Match report — vs {opp}"]
            if date:
                header_bits.append(date)
            if comp:
                header_bits.append(comp)
            block = [" | ".join(header_bits)]
            result_line = " ".join(b for b in (result, f"({score})" if score else "") if b).strip()
            if result_line:
                block.append(result_line)
            summary = str(report.get("summary") or "").strip()
            if summary:
                block.append("")
                block.append(summary)

            lineups = report.get("lineups")
            if isinstance(lineups, list) and lineups:
                ranked = sorted(
                    lineups,
                    key=lambda l: (l.get("score_diff") if isinstance(l.get("score_diff"), (int, float)) else -999),
                    reverse=True,
                )
                top = ranked[:3]
                if top:
                    block.append("")
                    block.append("Key lineups (by net differential):")
                    for l in top:
                        players = str(l.get("lineup_players") or "lineup").strip()
                        diff = l.get("score_diff")
                        diff_s = (
                            f"{'+' if isinstance(diff, (int, float)) and diff > 0 else ''}{diff}"
                            if diff is not None
                            else "n/a"
                        )
                        toc = str(l.get("time_on_court") or "").strip()
                        block.append(f"- {players}: net {diff_s}{f' in {toc}' if toc else ''}")
            blocks.append("\n".join(block))

        sources = [
            {"match_name": f"vs {r.get('opponent_name')}", "report_type": "Match Report", "source": "database"}
            for r in selected[:3]
        ]
        return self._phrase_db_answer(question, "\n\n".join(blocks)), sources

    def _answer_lineup_analysis(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        rows = client.get_lineup_analysis(project_id)
        if rows is None:
            return None
        if not rows:
            return "No lineup on/off analysis has been imported for this team yet.", []
        # Rank by score differential (net), then by time on court.
        ranked = sorted(
            rows,
            key=lambda r: (r.get("score_diff") if isinstance(r.get("score_diff"), (int, float)) else -999,
                           r.get("time_seconds") or 0),
            reverse=True,
        )
        lines = ["Most effective lineups (by net point differential):", ""]
        for i, r in enumerate(ranked[:8], 1):
            players = str(r.get("lineup_players") or "lineup").strip()
            diff = r.get("score_diff")
            diff_s = f"{'+' if isinstance(diff, (int, float)) and diff > 0 else ''}{diff}" if diff is not None else "n/a"
            toc = str(r.get("time_on_court") or "").strip()
            pf, pa = r.get("points_for"), r.get("points_against")
            pts = f", {pf}-{pa}" if pf is not None and pa is not None else ""
            opp = str(r.get("opponent_name") or "").strip()
            opp_bit = f" vs {opp}" if opp else ""
            lines.append(f"{i}. {players}: net {diff_s}{pts} in {toc or 'n/a'}{opp_bit}")
        return self._phrase_db_answer(question, "\n".join(lines)), []

    def _answer_coaching_lineups(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        lineups = client.get_coaching_lineups(project_id)
        if lineups is None:
            return None
        if not lineups:
            return "No tactical lineups have been saved for this team yet.", []
        latest = lineups[0]
        title = str(latest.get("title") or "Untitled lineup")
        lines = [f"Latest tactical lineup: {title}"]
        if latest.get("formation"):
            lines.append(f"Formation: {latest['formation']}")
        if latest.get("game_model"):
            lines.append(f"Game model: {latest['game_model']}")
        players = latest.get("players")
        if isinstance(players, list) and players:
            lines.append("")
            lines.append("Players:")
            for p in players:
                if not isinstance(p, dict):
                    continue
                nm = str(p.get("name") or "Unknown")
                pos = str(p.get("position") or "").strip()
                unit = str(p.get("unit") or "").strip()
                tags = " / ".join([t for t in (pos, unit) if t])
                instr = str(p.get("instructions") or "").strip()
                line = f"- {nm}" + (f" ({tags})" if tags else "")
                if instr:
                    line += f": {instr}"
                lines.append(line)
        if latest.get("tactical_notes"):
            lines.extend(["", f"Notes: {latest['tactical_notes']}"])
        if len(lineups) > 1:
            others = ", ".join(str(l.get("title") or "Untitled") for l in lineups[1:6])
            lines.extend(["", f"Other lineups: {others}"])
        return self._phrase_db_answer(question, "\n".join(lines)), []

    def _answer_coach_notes(self, project_id: str, question: str) -> tuple[str, list[Any]] | None:
        client = self._app_data_client()
        if client is None:
            return None
        notes = client.get_coach_notes(project_id)
        if notes is None:
            return None
        if not notes:
            return "No coach notes have been recorded for this team yet.", []
        lines = ["Recent coach notes:", ""]
        for n in notes[:10]:
            author = str(n.get("author_name") or "Coach")
            role = str(n.get("author_role") or "").strip()
            when = self._fmt_dt(n.get("created_at"))
            body = str(n.get("body") or "").strip()
            who = f"{author} ({role})" if role else author
            lines.append(f"- {when} — {who}: {body}")
        return self._phrase_db_answer(question, "\n".join(lines)), []

    def _prediction_summary_lines(self, project_id: str) -> list[str]:
        """Top projected players as short strings, or [] if no trained model."""
        service = self._prediction_service()
        if service is None:
            return []
        try:
            predictions = service.load_predictions(project_id)
        except Exception:
            return []
        if predictions is None or predictions.empty:
            return []
        name_col = next((c for c in ("Name", "name", "player", "Player", "player_name") if c in predictions.columns), None)
        pred_col = next((c for c in ("predicted_EF", "predicted_ef", "prediction", "predicted", "pred_EF") if c in predictions.columns), None)
        if name_col is None or pred_col is None:
            return []
        ranked = (
            predictions[[name_col, pred_col]]
            .dropna(subset=[pred_col])
            .groupby(name_col, as_index=False)[pred_col]
            .mean()
            .sort_values(pred_col, ascending=False)
            .head(5)
        )
        out: list[str] = []
        for _, row in ranked.iterrows():
            try:
                value = f"{float(row[pred_col]):.1f}"
            except (TypeError, ValueError):
                value = str(row[pred_col])
            out.append(f"{row[name_col]} (proj. EF {value})")
        return out

    def _answer_plan_advice(self, project_id: str, question: str, team: str) -> tuple[str, list[Any]] | None:
        """
        Plan-improvement synthesis (Phase 9.5, advise-only): gather the current plan +
        live injuries + fitness + the model's projection, and reason over all of them
        to suggest concrete improvements. Never writes the plan back. Returns None if
        the app integration is off so the question falls through to normal routing.
        """
        client = self._app_data_client()
        if client is None:
            return None
        plans = client.get_plans(project_id)
        if plans is None:
            return None
        if not plans:
            return (
                "I don't see a saved coaching plan for this team yet, so there's nothing to "
                "suggest improvements on. Add a plan and I'll review it against your stats, "
                "injuries, and projections."
            ), []
        plan = plans[0]
        injuries = client.get_injuries(project_id) or []
        fitness = client.get_fitness(project_id) or []
        projection_lines = self._prediction_summary_lines(project_id)

        if self.groq_client.is_configured():
            advice = self._plan_advice_with_groq(question, plan, injuries, fitness, projection_lines)
            if advice:
                return advice, []
        return self._plan_advice_fallback(plan, injuries, fitness, projection_lines), []

    def _plan_advice_with_groq(
        self,
        question: str,
        plan: dict[str, Any],
        injuries: list[dict[str, Any]],
        fitness: list[dict[str, Any]],
        projection_lines: list[str],
    ) -> str | None:
        plan_text = (
            f"Title: {plan.get('title') or 'Untitled'}\n"
            f"Description: {plan.get('description') or ''}\n"
            f"Content:\n{plan.get('content') or ''}"
        )
        inj = "; ".join(
            f"{i.get('name') or 'player'}: {i.get('injury_type') or 'injury'}" for i in injuries[:15]
        ) or "none recorded"
        fit = "; ".join(
            f"{f.get('name') or 'player'} endurance {f.get('endurance_score')}"
            for f in fitness[:10]
            if f.get("endurance_score") is not None
        ) or "no fitness data"
        proj = "; ".join(projection_lines) or "no model projections available"
        system = (
            "You are an assistant coach. Suggest concrete improvements to a team's coaching "
            "plan, grounded ONLY in the data provided (plan, current injuries, fitness, model "
            "projections). Do not invent players or numbers. Be specific and concise. You are "
            "advising only — do not claim to have changed the plan."
        )
        prompt = (
            f"Coach question:\n{question}\n\n"
            f"Current plan:\n{plan_text}\n\n"
            f"Current injuries: {inj}\n"
            f"Fitness signals: {fit}\n"
            f"Model projections (next match): {proj}\n\n"
            "Suggest 3-5 concrete improvements to the plan that account for the injuries, "
            "fitness, and projections above. Reference specific players where relevant."
        )
        try:
            out = self.groq_client.generate_text(prompt, system=system, temperature=0.2, max_tokens=700)
            return out if out.strip() else None
        except Exception:
            return None

    @staticmethod
    def _plan_advice_fallback(
        plan: dict[str, Any],
        injuries: list[dict[str, Any]],
        fitness: list[dict[str, Any]],
        projection_lines: list[str],
    ) -> str:
        lines = [f"Reviewing plan: {plan.get('title') or 'Untitled'}", ""]
        if injuries:
            names = ", ".join(str(i.get("name") or "a player") for i in injuries[:10])
            lines.append(
                f"- {len(injuries)} player(s) currently injured ({names}). Adjust load and avoid "
                "drills that risk re-injury; plan around their absence."
            )
        else:
            lines.append("- No current injuries — the squad is at full availability for this plan.")
        if projection_lines:
            lines.append(
                f"- Model projects these players strongest next match: {', '.join(projection_lines[:5])}. "
                "Build key roles around them."
            )
        low_endurance = [
            f for f in fitness
            if isinstance(f.get("endurance_score"), (int, float)) and float(f.get("endurance_score")) < 50
        ]
        if low_endurance:
            names = ", ".join(str(f.get("name") or "a player") for f in low_endurance[:10])
            lines.append(f"- Lower endurance flagged for: {names}. Consider conditioning blocks or managed minutes.")
        lines.extend(["", "(Suggestions are advisory — edit the plan in the app to apply them.)"])
        return "\n".join(lines)

    def _answer_analytics(
        self,
        parsed: dict[str, Any],
        df: pd.DataFrame,
        project_id: str,
        question: str,
        default_team: str,
    ) -> tuple[str, list[Any]]:
        query_type = str(parsed.get("type") or "")
        recipe = _plan_dict(parsed.get("analytics_recipe"))
        recipe_name = _recipe_name(recipe)

        if recipe_name and recipe_name not in {"player_comparison", "player_summary"}:
            result = self._execute_analytics_recipe(df, parsed, default_team=default_team)
        elif query_type == "player_comparison":
            result = compare_players(
                df,
                players=_as_list(parsed.get("players")),
                metric=str(parsed.get("metric") or "points"),
                team=parsed.get("team") or default_team,
            )
        elif query_type == "player_summary":
            players = _as_list(parsed.get("players"))
            if not players:
                result = {"answer": "I couldn't identify the player from that question. Please include the player's name.", "sources": []}
            else:
                result = summarize_player(df, player=players[0], team=parsed.get("team") or default_team)
        elif query_type == "squad_recommendation":
            result = recommend_squad(
                df,
                team=parsed.get("team") or default_team,
                top_n=_int_or_default(parsed.get("top_n"), 5),
                strategy=str(parsed.get("strategy") or "balanced"),
            )
        elif query_type == "player_opportunity_recommendation":
            result = recommend_more_minutes(
                df,
                team=parsed.get("team") or default_team,
                top_n=_int_or_default(parsed.get("top_n"), 3),
            )
        else:
            result = answer_stat_query(parsed, csv_path=self.store.box_score_csv(project_id), df=df)

        answer = str(result.get("answer") or "")
        if _env_enabled("ENABLE_ANALYTICS_LLM_FORMATTING", "0") and self.groq_client.is_configured():
            answer = self._format_analytics_answer(question, answer)
        return answer, list(result.get("sources") or [])

    def _execute_analytics_recipe(
        self,
        df: pd.DataFrame,
        plan: dict[str, Any],
        default_team: str = "EGY",
    ) -> dict[str, Any]:
        recipe = _plan_dict(plan.get("analytics_recipe"))
        name = _recipe_name(recipe)
        team = plan.get("team") or default_team
        top_n = _int_or_default(plan.get("top_n"), 3)

        if name == "opportunity_score":
            return recommend_more_minutes(df, team=team, top_n=top_n)

        if name == "weighted_score":
            metrics = _as_list(recipe.get("metrics")) or _as_list(plan.get("metrics"))
            weights = recipe.get("weights") if isinstance(recipe.get("weights"), dict) else {}
            return rank_weighted_score(
                df,
                metrics=metrics,
                weights={str(key): float(value) for key, value in weights.items()} if weights else None,
                team=team,
                top_n=top_n,
                title=recipe.get("title"),
                explanation=recipe.get("explanation"),
            )

        if name == "balanced_impact_score":
            return recommend_squad(
                df,
                team=team,
                top_n=_int_or_default(plan.get("top_n"), 5),
                strategy=str(plan.get("strategy") or "balanced"),
            )

        if name == "assist_turnover_context":
            result = rank_players(df, metric="assists", team=team, top_n=top_n)
            if result.get("answer"):
                result["answer"] = (
                    str(result["answer"])
                    + "\n\n### Context\n"
                    + "Use turnovers alongside assists when judging safe ball handling."
                )
            return result

        if name == "rank_by_metric":
            metric = str(recipe.get("ranking_metric") or plan.get("metric") or "")
            if metric == "opportunity_score":
                return recommend_more_minutes(df, team=team, top_n=top_n)
            if metric not in SUPPORTED_METRIC_NAMES:
                return {
                    "answer": _unsupported_answer(
                        {
                            "reason": f"{metric or 'requested metric'} is not available in the structured box-score columns",
                            "analytics_recipe": recipe,
                        }
                    ),
                    "sources": [],
                }
            return rank_players(
                df,
                metric=metric,
                team=team,
                top_n=top_n,
                metric_context=plan.get("metric_context") or recipe.get("metric_context"),
                min_attempts=_nonnegative_int(plan.get("min_attempts"), _nonnegative_int(recipe.get("min_attempts"), 0)),
            )

        if name == "unsupported":
            return {"answer": _unsupported_answer(plan), "sources": []}

        if name:
            mapped = dict(plan)
            mapped["analytics_recipe"] = _normalise_analytics_recipe(
                {"analytics_recipe": recipe},
                str(plan.get("type") or "stat_query"),
                str(plan.get("metric") or "") or None,
                _as_list(plan.get("metrics")),
            )
            if _recipe_name(mapped["analytics_recipe"]) != name:
                return self._execute_analytics_recipe(df, mapped, default_team=default_team)

        metric = str(plan.get("metric") or "points")
        if metric in SUPPORTED_METRIC_NAMES:
            return rank_players(df, metric=metric, team=team, top_n=top_n)
        return {"answer": _unsupported_answer(plan), "sources": []}

    def _format_analytics_answer(self, question: str, analytics_answer: str) -> str:
        system = (
            "You are a professional basketball analyst formatting an answer for a coach. "
            "Do not calculate statistics. Do not add new numbers, names, games, or sources."
        )
        prompt = f"""
Question:
{question}

Pandas analytics draft:
{analytics_answer}

Rewrite the draft clearly while preserving every fact and source.
"""
        try:
            formatted = self.groq_client.generate_text(prompt, system=system, temperature=0.1, max_tokens=700)
            return formatted if formatted.strip() else analytics_answer
        except (GroqConfigurationError, Exception):
            return analytics_answer

    def _rewrite_followup_with_groq(
        self,
        question: str,
        chronological_messages: list[Any],
        default_team: str,
    ) -> tuple[str, bool]:
        if not chronological_messages or not self.groq_client.is_configured():
            return question, False
        # Phase 2g: bound the history fed to the rewrite at the last N messages
        # (FOLLOWUP_HISTORY_TURNS, default 6). Caps token growth and latency on long
        # multi-turn sessions. FOLLOWUP_SUMMARIZE (default off) is reserved for later
        # summarization of older turns; the rolling window is enough for now.
        history_turns = _env_int("FOLLOWUP_HISTORY_TURNS", 6)
        recent = [
            {
                "role": getattr(message, "role", ""),
                "content": getattr(message, "content", ""),
                "parsed_intent": getattr(message, "parsed_intent", {}),
            }
            for message in chronological_messages[-history_turns:]
        ]
        prompt = f"""
Rewrite the current coach question into a standalone basketball analytics/RAG question.
Only rewrite if the current question is a follow-up to the recent messages.
If it is already standalone or unclear, return it unchanged.

Default team: {default_team}
Recent messages:
{recent}

Current question:
{question}

Return JSON only:
{{"rewritten_question":"..."}}
"""
        try:
            parsed = self.groq_client.generate_json(prompt, temperature=0, max_tokens=180)
        except Exception:
            return question, False
        if not parsed:
            return question, False
        rewritten = str(parsed.get("rewritten_question") or "").strip()
        if rewritten and rewritten.lower() != question.lower():
            return rewritten, True
        return question, False

    def _planning_prompt(
        self,
        question: str,
        project_context: ProjectContext,
        available_players: list[str],
        available_columns: list[str],
        default_team: str,
        memory_context: list[dict[str, Any]] | None = None,
    ) -> str:
        player_hint = ", ".join(available_players[:100])
        team_hint = ", ".join(project_context.available_teams or [default_team])
        column_hint = ", ".join(available_columns)
        memory_hint = memory_context or []
        supported_recipes = ", ".join(sorted(SUPPORTED_ANALYTICS_RECIPES))
        return f"""
Return JSON only. Do not answer the coach. Do not calculate statistics.

Available CSV columns:
{column_hint}

Supported analytics recipes:
{supported_recipes}

Available teams:
{team_hint}

Available players:
{player_hint}

Previous chat memory:
{memory_hint}

Required JSON shape:
{{
  "type": "stat_query | player_comparison | player_summary | squad_recommendation | analytic_recommendation | general_pdf_question | clarification | unsupported",
  "route": "analytics | rag | clarification | unsupported",
  "team": "{default_team}",
  "players": [],
  "top_n": 3,
  "metric": "efficiency",
  "metrics": ["efficiency"],
  "analytics_recipe": {{
    "name": "rank_by_metric",
    "ranking_metric": "efficiency",
    "sort": "desc",
    "filters": {{}},
    "explanation": "Rank players by efficiency because the coach is asking for impact."
  }},
  "reason": "short explanation of how the question was interpreted"
}}

Planning rules:
- If the answer can be derived from available CSV columns, route to analytics.
- If it needs narrative text or report context, route to rag.
- If it needs unavailable data, route unsupported.
- If it is ambiguous, route clarification and include "clarification_question".
- Groq only plans. Pandas calculates all numeric results later.

Semantic analytics concepts, not exhaustive:
- top scorers -> stat_query, rank_by_metric, ranking_metric points.
- compare players in assists -> player_comparison, metrics ["assists"], matched players.
- more minutes, deserves playing time, underused, more chances, productive in limited minutes -> analytic_recommendation, opportunity_score, metrics ["efficiency","points","rebounds","assists","steals","blocks","minutes_seconds"], ranking_metric "opportunity_score". Do not use metric "minutes".
- defensive pressure, pressure guards, take the ball, force turnovers -> rank_by_metric steals with metric_context "ball_pressure".
- protect the paint, rim protection, stop inside scoring -> weighted_score using blocks and defensive_rebounds with weights {{"blocks":2,"defensive_rebounds":1}}.
- best creators, playmakers -> rank_by_metric assists, show_context ["turnovers"].
- safe ball handlers -> assist_turnover_context using assists and turnovers.
- 3PT/threes -> three_point_shooting. 2PT/two pointers -> two_point_shooting. free throws -> free_throw_percentage.
- generic "best shooters" is ambiguous; ask whether 3PT, 2PT, FT, or overall FG%.
- best all-around, strongest lineup, best squad, starting five -> balanced_impact_score or squad_recommendation.
- played the most minutes, top players by minutes -> stat_query, rank_by_metric, ranking_metric minutes.
- deserves more minutes -> analytic_recommendation, opportunity_score, metric null.
- heart rate, speed tracking, recovery, height, weight, practice form, injury status -> unsupported unless the column is available.

Supported box-score metric aliases:
points, rebounds, offensive_rebounds, defensive_rebounds, assists, turnovers, steals, blocks,
plus_minus, efficiency, minutes, minutes_seconds, field_goal_percentage,
two_point_shooting, two_made, two_attempted, two_percentage,
three_point_shooting, three_made, three_attempted, three_percentage,
free_throw_percentage, ft_made, ft_attempted.

Current question:
{question}
"""

    def _classification_prompt(
        self,
        question: str,
        project_context: ProjectContext,
        available_players: list[str],
        default_team: str,
        memory_context: list[dict[str, Any]] | None = None,
    ) -> str:
        player_hint = ", ".join(available_players[:80])
        team_hint = ", ".join(project_context.available_teams or [default_team])
        memory_hint = memory_context or []
        original_question = project_context.original_question or question
        rewritten_line = (
            f"Rewritten standalone question: {question}"
            if original_question.strip().lower() != question.strip().lower()
            else "Rewritten standalone question: same as current question"
        )
        return f"""
Return one JSON object only. Do not answer the user. Do not calculate statistics.

Supported routes:
- analytics
- rag
- clarification

Supported types:
- stat_query
- player_comparison
- player_summary
- player_opportunity_recommendation
- squad_recommendation
- general_pdf_question
- play_by_play_question
- lineup_question
- plus_minus_question
- player_comparison_needs_clarification

Supported metrics:
points, rebounds, offensive_rebounds, defensive_rebounds, assists, steals, blocks, turnovers, minutes,
plus_minus, efficiency, three_point_shooting, three_made, three_attempted, three_percentage,
two_point_shooting, two_made, two_attempted, two_percentage, field_goal_percentage,
free_throw_percentage, ft_made, ft_attempted.

Return this shape, keeping irrelevant fields null or empty:
{{
  "type": "stat_query",
  "route": "analytics",
  "metric": "steals",
  "team": "{default_team}",
  "players": [],
  "top_n": 3,
  "aggregation": "sum",
  "group_by": "player",
  "min_attempts": 0,
  "reason": "short routing reason"
}}

For clarification, use:
{{
  "type": "player_comparison_needs_clarification",
  "route": "clarification",
  "metric": "rebounds",
  "team": "{default_team}",
  "players": ["matched player names only"],
  "matched_players": ["matched player names only"],
  "unmatched_phrases": ["unmatched player phrase"],
  "suggestions": {{"unmatched player phrase": "closest available player"}},
  "top_n": null,
  "aggregation": "sum",
  "group_by": "player",
  "min_attempts": 0,
  "reason": "comparison needs clarification"
}}

For more-minutes recommendations, use:
{{
  "type": "player_opportunity_recommendation",
  "route": "analytics",
  "metric": null,
  "team": "{default_team}",
  "players": [],
  "top_n": 3,
  "strategy": "underused_high_impact",
  "aggregation": "sum",
  "group_by": "player",
  "min_attempts": 0,
  "reason": "recommend underused high-impact players for more minutes"
}}

Available teams:
{team_hint}

Available player names:
{player_hint}

Relevant previous memory if available:
{memory_hint}

Metric rules:
- "pts" can mean points, but "2 pts", "2pt", "2 pointers", "two point" means two_point_shooting.
- "3 pts", "3pt", "threes", "three pointer" means three_point_shooting.
- "steels" is a typo for steals. "stl" and "stls" mean steals.
- "ast" and "asts" mean assists.
- "boards" means rebounds.
- "free throw", "FT", "FT%" means free_throw_percentage unless the question explicitly asks made or attempted.
- "force turnovers", "take the ball away", "pressure ball handlers", "catch the ball from them" means steals / ball pressure, not turnovers committed.
- "top 2 players in points" means metric=points and top_n=2.
- "give me the top 2 pointers" means metric=two_point_shooting and top_n=2.
- "defensive team" alone does not mean defensive_rebounds.
- "defensive rebounders" means defensive_rebounds.
- "offensive rebounders" means offensive_rebounds.

Intent rules:
1. Ranking/stat questions use type="stat_query", route="analytics".
2. Player comparisons use type="player_comparison", route="analytics".
3. Player summaries use type="player_summary", route="analytics".
4. Squad or lineup recommendations use type="squad_recommendation", route="analytics".
5. General game/report summaries use type="general_pdf_question", route="rag".
6. Play-by-play questions use type="play_by_play_question", route="rag".
7. Lineup report questions use type="lineup_question", route="rag".
8. Plus/minus report explanations use type="plus_minus_question", route="rag".
9. If a comparison has fewer than two confidently matched player names, use route="clarification".
10. If the question says "deserve more minutes", "should get more minutes", "underused", "more playing time", "give more minutes", "bench players should play more", "need more opportunities", or "productive in limited minutes", use type="player_opportunity_recommendation", route="analytics", strategy="underused_high_impact", metric=null, top_n=3.

Minutes distinction:
- "Who played the most minutes?" or "top players by minutes" means type="stat_query", metric="minutes".
- "Who deserves more minutes?" means type="player_opportunity_recommendation", metric=null. Do not classify it as metric="minutes".

Important:
- If route=analytics, Pandas will calculate the answer later. You must never answer directly.
- If route=rag, retrieval will happen later and Groq will answer only from retrieved PDF chunks.
- Return JSON only.

Current question:
{original_question}

{rewritten_line}
"""
