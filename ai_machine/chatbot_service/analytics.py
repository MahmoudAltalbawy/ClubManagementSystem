from __future__ import annotations

import re
from difflib import SequenceMatcher
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd

from language_utils import expand_query_for_matching
from extract_pdfs import BOX_SCORE_CSV


SUPPORTED_METRICS = {
    "points": "points",
    "rebounds": "total_rebounds",
    "defensive_rebounds": "defensive_rebounds",
    "offensive_rebounds": "offensive_rebounds",
    "assists": "assists",
    "steals": "steals",
    "blocks": "blocks",
    "turnovers": "turnovers",
    "plus_minus": "plus_minus",
    "efficiency": "efficiency",
    "minutes": "minutes_seconds",
    "two_point_shooting": "two_point_shooting",
    "two_made": "two_made",
    "two_attempted": "two_attempted",
    "two_percentage": "two_percentage",
    "three_point_shooting": "three_point_shooting",
    "three_made": "three_made",
    "three_attempted": "three_attempted",
    "three_percentage": "three_percentage",
    "field_goal_percentage": "field_goal_percentage",
    "free_throw_percentage": "free_throw_percentage",
    "ft_made": "ft_made",
    "ft_attempted": "ft_attempted",
}

METRIC_LABELS = {
    "points": "points",
    "total_rebounds": "rebounds",
    "defensive_rebounds": "defensive rebounds",
    "offensive_rebounds": "offensive rebounds",
    "assists": "assists",
    "steals": "steals",
    "blocks": "blocks",
    "turnovers": "turnovers",
    "plus_minus": "plus/minus",
    "efficiency": "efficiency",
    "minutes_seconds": "minutes",
    "two_point_shooting": "2PT shooting",
    "two_made": "2PT made",
    "two_attempted": "2PT attempts",
    "two_percentage": "2PT%",
    "three_point_shooting": "3PT shooting",
    "three_made": "3PT made",
    "three_attempted": "3PT attempts",
    "three_percentage": "3PT%",
    "field_goal_percentage": "FG%",
    "free_throw_percentage": "FT%",
    "ft_made": "free throws made",
    "ft_attempted": "free throw attempts",
}

TITLE_METRIC_LABELS = {
    "points": "Points",
    "total_rebounds": "Rebounds",
    "defensive_rebounds": "Defensive Rebounds",
    "offensive_rebounds": "Offensive Rebounds",
    "assists": "Assists",
    "steals": "Steals",
    "blocks": "Blocks",
    "turnovers": "Turnovers",
    "plus_minus": "Plus/Minus",
    "efficiency": "Efficiency",
    "minutes_seconds": "Minutes",
    "two_point_shooting": "2PT Shooting",
    "two_made": "2PT made",
    "two_attempted": "2PT attempts",
    "two_percentage": "2PT%",
    "three_point_shooting": "3PT Shooting",
    "three_made": "3PT made",
    "three_attempted": "3PT attempts",
    "three_percentage": "3PT%",
    "field_goal_percentage": "FG%",
    "free_throw_percentage": "FT%",
    "ft_made": "Free Throws Made",
    "ft_attempted": "Free Throw Attempts",
}

COUNTING_COLUMNS = [
    "fg_made",
    "fg_attempted",
    "two_made",
    "two_attempted",
    "three_made",
    "three_attempted",
    "ft_made",
    "ft_attempted",
    "offensive_rebounds",
    "defensive_rebounds",
    "total_rebounds",
    "assists",
    "turnovers",
    "steals",
    "blocks",
    "personal_fouls",
    "fouls_drawn",
    "plus_minus",
    "efficiency",
    "minutes_seconds",
    "points",
]

TEAM_ALIASES = {
    "egypt": "EGY",
    "egy": "EGY",
    "angola": "ANG",
    "ang": "ANG",
    "mali": "MLI",
    "mli": "MLI",
    "uganda": "UGA",
    "uga": "UGA",
}

COMPARISON_PATTERNS = [
    r"\bcompare\b",
    r"\bcomparison\b",
    r"\bversus\b",
    r"\bvs\.?\b",
    r"\bdifference between\b",
    r"\bwho is better between\b",
]

RANKING_PATTERNS = [
    r"\btop\b",
    r"\bbest\b",
    r"\bhighest\b",
    r"\bmost\b",
    r"\brank\b",
    r"\bleaders?\b",
]

PLAYER_SUMMARY_PATTERNS = [
    r"\bshow\b",
    r"\bstats?\b",
    r"\bfull stats?\b",
    r"\bperform(?:ed|ance)?\b",
    r"\bhow did\b",
]

SQUAD_RECOMMENDATION_PATTERNS = [
    r"\bsuggest\b.*\bsquad\b",
    r"\brecommend\b.*\bsquad\b",
    r"\bbest\s+squad\b",
    r"\bsquad\s+for\s+(?:the\s+)?next\s+game\b",
    r"\bstarting\s+(?:five|5)\b",
    r"\bbest\s+(?:five|5)\b",
    r"\bwho\s+should\s+start\b",
    r"\bbest\s+lineup\b",
    r"\bsuggest\b.*\blineup\b",
    r"\brecommend\b.*\blineup\b",
]

PLAYER_OPPORTUNITY_PATTERNS = [
    r"\bdeserve(?:s)?\s+more\s+(?:minutes|playing\s+time)\b",
    r"\bshould\s+(?:get|play|receive)\s+more\s+(?:minutes|playing\s+time)\b",
    r"\bwho\s+should\s+get\s+more\s+minutes\b",
    r"\bwho\s+should\s+we\s+give\s+more\s+minutes\s+to\b",
    r"\bgive\s+more\s+minutes\s+to\b",
    r"\bunderused\b",
    r"\bunder\s+used\b",
    r"\bbench\s+players?\s+should\s+play\s+more\b",
    r"\bplayers?\s+need\s+more\s+opportunit(?:y|ies)\b",
    r"\bmore\s+opportunit(?:y|ies)\b",
    r"\bproductive\s+in\s+limited\s+minutes\b",
    r"\blimited\s+minutes\b.*\bproductive\b",
    r"\bmore\s+playing\s+time\b",
]

BALL_PRESSURE_PATTERNS = [
    r"\bcatch(?:es|ing)?\s+the\s+ball\s+from\s+them\b",
    r"\btake(?:s|ing)?\s+the\s+ball\s+from\s+them\b",
    r"\btake(?:s|ing)?\s+the\s+ball\s+away\b",
    r"\bsteal(?:s|ing)?\s+the\s+ball\b",
    r"\bsteal(?:s|ing)?\s+from\s+them\b",
    r"\bget(?:s|ting)?\s+the\s+ball\s+from\s+them\b",
    r"\bwin(?:s|ning)?\s+the\s+ball\b",
    r"\bforce(?:s|ing)?\s+steals?\b",
    r"\bpressure\s+ball\s+handlers?\b",
    r"\bpressure\s+handlers?\b",
    r"\bdisrupt\s+handlers?\b",
    r"\bdisrupt\s+their\s+guards?\b",
    r"\bdefensive\s+pressure\b",
    r"\bball\s+pressure\b",
    r"\bforce(?:s|ing)?\s+turnovers?\b",
]

BALL_HANDLING_CONTEXT_PATTERNS = [
    r"\bgood\s+handling\s+skills?\b",
    r"\bball\s+handling\b",
    r"\bball\s+handlers?\b",
]

TAKEAWAY_CONTEXT_PATTERNS = [
    r"\bplayers?\b",
    r"\bbest\b",
    r"\btop\b",
    r"\bwho\s+can\b",
    r"\bcatch(?:es|ing)?\b",
    r"\btake(?:s|ing)?\b",
    r"\bget(?:s|ting)?\b",
    r"\bsteal(?:s|ing)?\b",
    r"\bpressure\b",
    r"\bdisrupt\b",
    r"\bforce\b",
]

FREE_THROW_CONTEXT_PATTERNS = [
    r"\bfree\s+throws?\b",
    r"\bft\b",
    r"\bfoul\s+shots?\b",
    r"\bcharity\s+stripe\b",
]

FREE_THROW_ATTEMPT_PATTERNS = [
    r"\battempt(?:ed|s|ing)?\b",
    r"\btook\b",
    r"\btaken\b",
    r"\btakes?\b",
]

FREE_THROW_MADE_PATTERNS = [
    r"\bmade\b",
    r"\bmake(?:s|ing)?\b",
    r"\bhit(?:s|ting)?\b",
    r"\bconvert(?:ed|s|ing)?\b",
]


def load_box_scores(csv_path: Path = BOX_SCORE_CSV) -> pd.DataFrame:
    if not csv_path.exists():
        return pd.DataFrame()
    df = pd.read_csv(csv_path)
    for column in COUNTING_COLUMNS:
        if column in df.columns:
            df[column] = pd.to_numeric(df[column], errors="coerce").fillna(0)
    return enrich_box_scores(df)


def minutes_to_seconds(value: Any) -> int:
    if pd.isna(value):
        return 0
    parts = str(value).strip().split(":")
    if len(parts) != 2:
        return 0
    try:
        return int(parts[0]) * 60 + int(parts[1])
    except ValueError:
        return 0


def safe_percentage(made: pd.Series, attempted: pd.Series) -> pd.Series:
    attempted = attempted.replace(0, np.nan)
    return (made / attempted * 100).round(1)


def enrich_box_scores(df: pd.DataFrame) -> pd.DataFrame:
    if df.empty:
        return df
    enriched = df.copy()
    enriched["three_percentage"] = safe_percentage(enriched["three_made"], enriched["three_attempted"])
    enriched["two_percentage"] = safe_percentage(enriched["two_made"], enriched["two_attempted"])
    enriched["field_goal_percentage"] = safe_percentage(enriched["fg_made"], enriched["fg_attempted"])
    enriched["free_throw_percentage"] = safe_percentage(enriched["ft_made"], enriched["ft_attempted"])
    enriched["minutes_seconds"] = enriched["minutes"].apply(minutes_to_seconds)
    enriched["games_played"] = 1
    if "source_pdf" not in enriched.columns:
        enriched["source_pdf"] = ""
    return enriched


def normalise_team(team: str | None) -> str | None:
    if not team:
        return None
    cleaned = str(team).strip().lower()
    if cleaned in {"all", "any", "none", "null"}:
        return None
    return TEAM_ALIASES.get(cleaned, cleaned.upper())


def normalize_text(value: str) -> str:
    return re.sub(r"\s+", " ", re.sub(r"[^a-z0-9]+", " ", value.lower())).strip()


def ordered_unique(values: list[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for value in values:
        if value not in seen:
            seen.add(value)
            result.append(value)
    return result


def available_players_from_df(df: pd.DataFrame, team: str | None = None) -> list[str]:
    if df.empty or "player_name" not in df.columns:
        return []
    scoped = df
    if team and "team" in scoped.columns:
        scoped = scoped[scoped["team"].astype(str).str.upper() == team.upper()]
    return sorted(scoped["player_name"].dropna().astype(str).unique().tolist())


def extract_player_names(question: str, available_players: list[str], threshold: float = 0.82) -> list[str]:
    if not available_players:
        return []

    q_norm = normalize_text(question)
    q_tokens = q_norm.split()
    matches: list[tuple[int, float, str]] = []

    for player in available_players:
        player_norm = normalize_text(player)
        if not player_norm:
            continue
        index = q_norm.find(player_norm)
        if index >= 0:
            matches.append((index, 1.0, player))

    token_to_players: dict[str, list[str]] = {}
    for player in available_players:
        for token in normalize_text(player).split():
            if len(token) >= 5:
                token_to_players.setdefault(token, []).append(player)
    for index, token in enumerate(q_tokens):
        players = token_to_players.get(token, [])
        if len(players) == 1:
            matches.append((index, 0.91, players[0]))

    max_name_tokens = max((len(normalize_text(player).split()) for player in available_players), default=1)
    spans: list[tuple[int, str]] = []
    for start in range(len(q_tokens)):
        for length in range(2, max_name_tokens + 1):
            span_tokens = q_tokens[start : start + length]
            if len(span_tokens) == length:
                spans.append((start, " ".join(span_tokens)))

    for player in available_players:
        player_norm = normalize_text(player)
        best: tuple[int, float] | None = None
        for start, span in spans:
            score = SequenceMatcher(None, span, player_norm).ratio()
            if score >= threshold and (best is None or score > best[1]):
                best = (start, score)
        if best:
            matches.append((best[0], best[1], player))

    matches.sort(key=lambda item: (item[0], -item[1]))
    return ordered_unique([player for _, _, player in matches])


def has_any_pattern(question: str, patterns: list[str]) -> bool:
    q = question.lower()
    return any(re.search(pattern, q) for pattern in patterns)


def is_ball_pressure_question(question: str) -> bool:
    q = question.lower().strip()
    if any(re.search(pattern, q) for pattern in BALL_PRESSURE_PATTERNS):
        return True
    has_handling_context = any(re.search(pattern, q) for pattern in BALL_HANDLING_CONTEXT_PATTERNS)
    has_takeaway_context = any(re.search(pattern, q) for pattern in TAKEAWAY_CONTEXT_PATTERNS)
    return has_handling_context and has_takeaway_context


def is_squad_recommendation_question(question: str) -> bool:
    q = question.lower().strip()
    return any(re.search(pattern, q) for pattern in SQUAD_RECOMMENDATION_PATTERNS)


def is_player_opportunity_question(question: str) -> bool:
    q = question.lower().strip()
    return any(re.search(pattern, q) for pattern in PLAYER_OPPORTUNITY_PATTERNS)


def detect_squad_strategy(question: str) -> str:
    q = question.lower().strip()
    if re.search(r"\bdefen[sc]e\b|\bdefensive\b|\bstops?\b|\bpressure\b", q):
        return "defensive"
    if re.search(r"\bshoot(?:ing|ers?)?\b|\bspacing\b|\b3\s*pt\b|\bthree[-\s]?point\b", q):
        return "shooting"
    if re.search(r"\bfast\b|\btransition\b|\bpace\b|\brun\b", q):
        return "pace"
    return "balanced"


def extract_top_n(question: str, default: int = 3, most_one: bool = False) -> int:
    q = question.lower().strip()
    top_n_match = re.search(r"\b(?:top|best|highest)\s+(\d+)\b|\b(\d+)\s+players\b|\b(?:five|5)\b", q)
    if top_n_match:
        numeric_group = next((group for group in top_n_match.groups() if group), None)
        return int(numeric_group) if numeric_group else 5
    if most_one and re.search(r"\bmost\b", q):
        return 1
    return default


def detect_free_throw_metric(question: str) -> str | None:
    q = question.lower().strip()
    if not any(re.search(pattern, q) for pattern in FREE_THROW_CONTEXT_PATTERNS):
        return None
    if any(re.search(pattern, q) for pattern in FREE_THROW_ATTEMPT_PATTERNS):
        return "ft_attempted"
    if any(re.search(pattern, q) for pattern in FREE_THROW_MADE_PATTERNS):
        return "ft_made"
    return "free_throw_percentage"


def stat_metric_patterns() -> list[tuple[str, list[str]]]:
    return [
        ("assists", [r"\bassists?\b", r"\bassisters?\b", r"\basts?\b", r"\bplaymakers?\b"]),
        ("steals", [r"\bsteals?\b", r"\bstealers?\b", r"\bstls?\b", r"\bsteels?\b"]),
        ("blocks", [r"\bblocks?\b", r"\bblk\b"]),
        ("turnovers", [r"\bturnovers?\b", r"\btov\b"]),
        ("defensive_rebounds", [
            r"\bdreb\b",
            r"\bdefensive\s+rebounds?\b",
            r"\bdefensive\s+rebounders?\b",
            r"\bdefensive\s+boards?\b",
        ]),
        ("offensive_rebounds", [
            r"\boreb\b",
            r"\boffensive\s+rebounds?\b",
            r"\boffensive\s+rebounders?\b",
            r"\boffensive\s+boards?\b",
        ]),
        ("rebounds", [
            r"\btotal rebounds?\b",
            r"\brebounds?\b",
            r"\brebounders?\b",
            r"\breb\b",
            r"\bboards?\b",
        ]),
        ("minutes", [r"\bminutes?\b", r"\bmins?\b", r"\bplaying time\b"]),
        ("plus_minus", [r"\bplus\s*/?\s*minus\b", r"\b\+/-\b"]),
        ("efficiency", [r"\befficiency\b", r"\beff\b"]),
        ("three_percentage", [
            r"\b3\s*pt\s*%\b",
            r"\b3\s*pts\s*%\b",
            r"\b3-point\s*%\b",
            r"\bthree[-\s]?point percentage\b",
            r"\b3pt percentage\b",
        ]),
        ("two_percentage", [
            r"\b2\s*pt\s*%\b",
            r"\b2-point\s*%\b",
            r"\btwo[-\s]?point percentage\b",
            r"\b2pt percentage\b",
        ]),
        ("field_goal_percentage", [r"\bfg\s*%\b", r"\bfield goal percentage\b"]),
        ("free_throw_percentage", [
            r"\bft\s*%(?!\w)",
            r"\bft\s+percent(?:age)?\b",
            r"\bfree\s+throw\s+percent(?:age)?\b",
            r"\bfree\s+throws?\s*%(?!\w)",
            r"\bfoul\s+shot\s+percent(?:age)?\b",
        ]),
        ("two_point_shooting", [
            r"\b2\s*pt\b",
            r"\b2\s*pts\b",
            r"\b2-point\b",
            r"\btwo[-\s]?point\b",
            r"\btwo[-\s]?pointer\b",
            r"\btwo[-\s]?pointers\b",
            r"\b2\s*pointers?\b",
        ]),
        ("three_point_shooting", [
            r"\b3\s*pt\b",
            r"\b3\s*pts\b",
            r"\b3-point\b",
            r"\bthree[-\s]?point\b",
            r"\bthree[-\s]?pointer\b",
            r"\bthrees\b",
            r"\b3\s*pointers?\b",
        ]),
        ("points", [r"\bpoints?\b", r"\bpts\b", r"\bscorers?\b"]),
    ]


def detect_stat_metric(question: str) -> str | None:
    q = expand_query_for_matching(question)
    if is_ball_pressure_question(q):
        return "steals"
    free_throw_metric = detect_free_throw_metric(q)
    if free_throw_metric:
        return free_throw_metric
    for metric_name, patterns in stat_metric_patterns():
        if any(re.search(pattern, q) for pattern in patterns):
            return metric_name
    return None


def parse_stat_question(question: str, default_team: str = "EGY") -> dict[str, Any]:
    q = expand_query_for_matching(question)

    top_n = extract_top_n(q, default=3, most_one=True)

    metric = detect_stat_metric(q) or "points"

    group_by = "team" if "which team" in q or "team has" in q or "teams" in q else "player"
    mentioned_team = next((code for alias, code in TEAM_ALIASES.items() if re.search(rf"\b{alias}\b", q)), None)
    team = mentioned_team
    if group_by == "player" and team is None and "all team" not in q and "all players" not in q:
        team = default_team

    min_attempts_match = re.search(r"(?:at least|min(?:imum)?)\s+(\d+)\s+(?:attempt|shot)", q)
    min_attempts = int(min_attempts_match.group(1)) if min_attempts_match else 0
    if metric == "free_throw_percentage" and min_attempts == 0:
        min_attempts = 2
    elif metric.endswith("_percentage") and min_attempts == 0:
        min_attempts = 5

    aggregation = "avg" if "per game" in q or "average" in q or "avg" in q else "sum"
    return {
        "type": "stat_query",
        "metric": metric,
        "team": team,
        "player": None,
        "top_n": top_n,
        "aggregation": aggregation,
        "min_attempts": min_attempts,
        "group_by": group_by,
        "route": "analytics",
        "metric_context": "ball_pressure" if metric == "steals" and is_ball_pressure_question(q) else None,
    }


def fallback_classify_stat_question(question: str, default_team: str = "EGY") -> dict[str, Any]:
    return parse_stat_question(question, default_team=default_team)


def parse_question(
    question: str,
    available_players: list[str] | None = None,
    default_team: str = "EGY",
) -> dict[str, Any]:
    q = expand_query_for_matching(question)
    team = next((code for alias, code in TEAM_ALIASES.items() if re.search(rf"\b{alias}\b", q)), default_team)
    available_players = available_players or []
    players = extract_player_names(question, available_players)
    metric = detect_stat_metric(question)
    is_comparison = has_any_pattern(question, COMPARISON_PATTERNS)
    is_ranking = has_any_pattern(question, RANKING_PATTERNS)

    if is_player_opportunity_question(question) and not is_comparison:
        return {
            "type": "player_opportunity_recommendation",
            "route": "analytics",
            "metric": None,
            "players": [],
            "team": team,
            "top_n": extract_top_n(question, default=3),
            "strategy": "underused_high_impact",
        }

    if is_squad_recommendation_question(question) and not is_comparison:
        return {
            "type": "squad_recommendation",
            "route": "analytics",
            "metric": None,
            "players": [],
            "team": team,
            "top_n": extract_top_n(question, default=5),
            "strategy": detect_squad_strategy(question),
        }

    if is_comparison and len(players) >= 2 and metric:
        return {
            "type": "player_comparison",
            "route": "analytics",
            "metric": metric,
            "players": players,
            "team": team,
            "top_n": None,
        }

    if players and not is_comparison and not is_ranking:
        return {
            "type": "player_summary",
            "route": "analytics",
            "metric": metric,
            "players": players[:1],
            "team": team,
            "top_n": None,
        }

    if metric:
        parsed = parse_stat_question(question, default_team=team)
        parsed["players"] = players
        return parsed

    return {
        "type": "general_pdf_question",
        "route": "rag",
        "metric": None,
        "players": players,
        "team": team,
        "top_n": None,
    }


def metric_columns(metric_key: str) -> tuple[str, str | None, str | None]:
    if metric_key in {"three_percentage", "three_point_shooting"}:
        return metric_key, "three_made", "three_attempted"
    if metric_key in {"two_percentage", "two_point_shooting"}:
        return metric_key, "two_made", "two_attempted"
    if metric_key == "field_goal_percentage":
        return metric_key, "fg_made", "fg_attempted"
    if metric_key == "free_throw_percentage":
        return metric_key, "ft_made", "ft_attempted"
    return metric_key, None, None


def selected_analytics_function(metric: str) -> str:
    metric_key = SUPPORTED_METRICS.get(metric, metric)
    if metric_key == "assists":
        return "rank_players_by_assists"
    if metric_key == "steals":
        return "rank_players_by_steals"
    if metric_key == "defensive_rebounds":
        return "rank_players_by_defensive_rebounds"
    if metric_key == "offensive_rebounds":
        return "rank_players_by_offensive_rebounds"
    if metric_key in {"two_made", "two_percentage", "two_point_shooting"}:
        return "rank_two_point_shooting"
    if metric_key in {"three_made", "three_percentage", "three_point_shooting"}:
        return "rank_three_point_shooting"
    if metric_key == "free_throw_percentage":
        return "rank_players_by_free_throw_percentage"
    if metric_key == "ft_made":
        return "rank_players_by_free_throws_made"
    if metric_key == "ft_attempted":
        return "rank_players_by_free_throw_attempts"
    if metric_key == "total_rebounds":
        return "rank_players_by_rebounds"
    if metric_key == "plus_minus":
        return "rank_players_by_plus_minus"
    return f"rank_by_{metric_key}"


def format_duration(total_seconds: Any) -> str:
    if pd.isna(total_seconds):
        return "0:00"
    seconds = int(total_seconds)
    return f"{seconds // 60}:{seconds % 60:02d}"


def rebounder_title(metric_key: str, team: str | None, top_n: int) -> str | None:
    if metric_key == "total_rebounds":
        label = "Rebounder" if top_n == 1 else "Rebounders"
    elif metric_key == "defensive_rebounds":
        label = "Defensive Rebounder" if top_n == 1 else "Defensive Rebounders"
    elif metric_key == "offensive_rebounds":
        label = "Offensive Rebounder" if top_n == 1 else "Offensive Rebounders"
    else:
        return None

    population = team or "All"
    if top_n == 1:
        return f"Top {population} {label}"
    return f"Top {top_n} {population} {label}"


def title_label_for_metric(metric_key: str, metric_context: str | None = None) -> str:
    if metric_key == "steals" and metric_context == "ball_pressure":
        return "Ball Pressure / Steals"
    return TITLE_METRIC_LABELS.get(metric_key, METRIC_LABELS.get(metric_key, metric_key))


def coach_context_explanation(metric_key: str, metric_context: str | None = None) -> str | None:
    if metric_key == "steals" and metric_context == "ball_pressure":
        return "These are the best available ball-pressure options based on steals in the uploaded reports."
    return None


def format_number(value: Any, suffix: str = "") -> str:
    if pd.isna(value):
        return "-"
    if isinstance(value, float) and not value.is_integer():
        return f"{value:.1f}{suffix}"
    return f"{int(value)}{suffix}"


def format_percentage(made: Any, attempted: Any) -> str:
    if attempted in (0, 0.0) or pd.isna(attempted):
        return "0.0%"
    return f"{(made / attempted * 100):.1f}%"


def aggregate_rows(df: pd.DataFrame, group_by: str, metric_key: str, aggregation: str) -> pd.DataFrame:
    keys = ["team"] if group_by == "team" else ["player_name", "team"]
    numeric_sums = df.groupby(keys, as_index=False)[COUNTING_COLUMNS + ["games_played"]].sum()
    sources = (
        df.groupby(keys)["source_pdf"]
        .apply(lambda values: sorted(set(str(value) for value in values if str(value))))
        .reset_index(name="sources")
    )
    matches = (
        df.groupby(keys)["match_name"]
        .apply(lambda values: sorted(set(str(value) for value in values if str(value))))
        .reset_index(name="matches")
    )
    result = numeric_sums.merge(sources, on=keys, how="left").merge(matches, on=keys, how="left")

    result["three_percentage"] = safe_percentage(result["three_made"], result["three_attempted"])
    result["two_percentage"] = safe_percentage(result["two_made"], result["two_attempted"])
    result["field_goal_percentage"] = safe_percentage(result["fg_made"], result["fg_attempted"])
    result["free_throw_percentage"] = safe_percentage(result["ft_made"], result["ft_attempted"])

    if aggregation in {"avg", "average", "per_game"} and metric_key not in {
        "two_percentage",
        "three_percentage",
        "field_goal_percentage",
        "free_throw_percentage",
    }:
        result["rank_value"] = (result[metric_key] / result["games_played"]).round(2)
    else:
        result["rank_value"] = result[metric_key]
    return result


def parse_game_dates(values: pd.Series) -> pd.Series:
    text = values.fillna("").astype(str).str.strip()
    parsed = pd.Series(pd.NaT, index=text.index, dtype="datetime64[ns]")
    iso_like = text.str.match(r"^\d{4}-\d{1,2}-\d{1,2}(?:[T\s]|$)")
    if iso_like.any():
        parsed.loc[iso_like] = pd.to_datetime(text.loc[iso_like], errors="coerce").dt.tz_localize(None)
    if (~iso_like).any():
        parsed.loc[~iso_like] = pd.to_datetime(text.loc[~iso_like], errors="coerce", dayfirst=True)
    return parsed


def sorted_games(df: pd.DataFrame) -> pd.DataFrame:
    sorted_df = df.copy()
    if "date" not in sorted_df.columns:
        sorted_df["date"] = ""
    if "match_name" not in sorted_df.columns:
        sorted_df["match_name"] = ""
    sorted_df["_date_sort"] = parse_game_dates(sorted_df["date"])
    sorted_df["_match_sort"] = sorted_df["match_name"].fillna("").astype(str)
    return sorted_df.sort_values(["_date_sort", "_match_sort"], na_position="last")


def format_game_metric(game: pd.Series, metric_key: str) -> str:
    _, made_col, attempted_col = metric_columns(metric_key)
    if metric_key in {"two_made", "two_point_shooting"}:
        return (
            f"{int(game['two_made'])}/{int(game['two_attempted'])} "
            f"({format_percentage(game['two_made'], game['two_attempted'])})"
        )
    if metric_key in {"three_made", "three_point_shooting"}:
        return (
            f"{int(game['three_made'])}/{int(game['three_attempted'])} "
            f"({format_percentage(game['three_made'], game['three_attempted'])})"
        )
    if made_col and attempted_col:
        return f"{int(game[made_col])}/{int(game[attempted_col])} ({format_percentage(game[made_col], game[attempted_col])})"
    if metric_key == "minutes_seconds":
        return format_duration(game[metric_key])
    return format_number(game[metric_key])


def build_breakdown(df: pd.DataFrame, row: pd.Series, group_by: str, metric_key: str, aggregation: str) -> list[str]:
    if group_by == "team":
        subset = df[df["team"] == row["team"]].copy()
        if "date" not in subset.columns:
            subset["date"] = ""
        if "match_name" not in subset.columns:
            subset["match_name"] = ""
        games = subset.groupby(["match_name", "date"], as_index=False)[COUNTING_COLUMNS].sum()
        games["three_percentage"] = safe_percentage(games["three_made"], games["three_attempted"])
        games["two_percentage"] = safe_percentage(games["two_made"], games["two_attempted"])
        games["field_goal_percentage"] = safe_percentage(games["fg_made"], games["fg_attempted"])
        games["free_throw_percentage"] = safe_percentage(games["ft_made"], games["ft_attempted"])
    else:
        games = df[(df["team"] == row["team"]) & (df["player_name"] == row["player_name"])].copy()

    breakdown: list[str] = []
    for _, game in sorted_games(games).iterrows():
        breakdown.append(f"{game['match_name']}: {format_game_metric(game, metric_key)}")
    return breakdown


def answer_stat_query(
    query: dict[str, Any],
    csv_path: Path = BOX_SCORE_CSV,
    df: pd.DataFrame | None = None,
) -> dict[str, Any]:
    # Phase 0: callers may pass a pre-built box-score frame (e.g. PDF ∪ DB) so the
    # stat lane works even when no CSV exists. Fall back to the CSV when not given.
    if df is None or df.empty:
        df = load_box_scores(csv_path)
    if df.empty:
        return {
            "answer": "I couldn't find box score data. Add FIBA Box Score PDFs and rebuild the extraction.",
            "sources": [],
        }

    metric = SUPPORTED_METRICS.get(str(query.get("metric", "points")), "points")
    if metric == "three_point_shooting":
        rank_metric = "three_made"
    elif metric == "two_point_shooting":
        rank_metric = "two_made"
    else:
        rank_metric = metric
    group_by = query.get("group_by") or ("team" if query.get("team") is None and "team" in str(query) else "player")
    group_by = "team" if group_by == "team" else "player"
    team = normalise_team(query.get("team"))
    player = str(query.get("player") or "").strip().lower()
    top_n = max(1, int(query.get("top_n") or 5))
    aggregation = str(query.get("aggregation") or "sum").lower()
    min_attempts = max(0, int(query.get("min_attempts") or 0))
    metric_context = query.get("metric_context")

    filtered = df.copy()
    if team:
        filtered = filtered[filtered["team"].str.upper() == team]
    if player:
        filtered = filtered[filtered["player_name"].str.lower().str.contains(player, regex=False)]

    if filtered.empty:
        return {
            "answer": "I couldn't find matching box score rows for that question.",
            "sources": [],
        }

    metric_key, made_col, attempted_col = metric_columns(metric)
    ranked = aggregate_rows(filtered, group_by, rank_metric, aggregation)
    if attempted_col and min_attempts:
        ranked = ranked[ranked[attempted_col] >= min_attempts]

    ranked = ranked.sort_values("rank_value", ascending=False).head(top_n)
    if ranked.empty:
        return {
            "answer": (
                "I found the metric, but no player or team met the minimum attempt filter "
                f"({min_attempts} attempts)."
            ),
            "sources": sorted(set(filtered["source_pdf"].dropna().astype(str))),
        }

    label = METRIC_LABELS.get(metric, METRIC_LABELS.get(metric_key, metric_key))
    title_label = title_label_for_metric(metric, str(metric_context) if metric_context else None)
    population = team if team and group_by == "player" else "all teams"
    aggregation_label = "per game" if aggregation in {"avg", "average", "per_game"} else "total"
    if metric_key.endswith("_percentage"):
        aggregation_label = "percentage"

    title_population = population if group_by == "player" else "all"
    rebound_title = rebounder_title(rank_metric, team, len(ranked)) if group_by == "player" else None
    if rebound_title:
        title = rebound_title
    elif group_by == "player" and len(ranked) == 1:
        title = f"Top {title_population} player for {title_label}"
    elif group_by == "player":
        title = f"Top {len(ranked)} {title_population} players for {title_label}"
    elif len(ranked) == 1:
        title = f"Top team for {title_label}"
    else:
        title = f"Top {len(ranked)} teams for {title_label}"
    ranking_sentence = f"Ranking uses {aggregation_label} {label}."
    if metric in {"two_made", "two_point_shooting"}:
        ranking_sentence = "Ranking uses total 2PT made, with attempts and percentage shown for context."
    lines = [
        "### Direct answer",
        title,
        ranking_sentence,
        "",
        "### Ranking",
    ]

    all_sources: set[str] = set()
    for rank, (_, row) in enumerate(ranked.iterrows(), start=1):
        name = row["team"] if group_by == "team" else f"{row['player_name']} ({row['team']})"
        games = int(row["games_played"])
        if made_col and attempted_col:
            headline_value = f"{int(row[made_col])}/{int(row[attempted_col])} ({format_percentage(row[made_col], row[attempted_col])})"
        elif aggregation in {"avg", "average", "per_game"}:
            headline_value = f"{float(row['rank_value']):.1f} per game ({int(row[rank_metric])} total in {games} games)"
        else:
            headline_value = format_number(row["rank_value"])

        if metric in {"two_made", "two_point_shooting"}:
            headline_value = (
                f"{int(row['two_made'])} made twos on {int(row['two_attempted'])} attempts "
                f"({format_percentage(row['two_made'], row['two_attempted'])})"
            )
        elif metric in {"three_made", "three_point_shooting"}:
            headline_value = (
                f"{int(row['three_made'])} made threes on {int(row['three_attempted'])} attempts "
                f"({format_percentage(row['three_made'], row['three_attempted'])})"
            )
        elif rank_metric == "minutes_seconds":
            headline_value = format_duration(row["rank_value"])

        lines.append(f"{rank}. **{name}** - {headline_value}")
        for item in build_breakdown(filtered, row, group_by, metric, aggregation):
            lines.append(f"   - {item}")
        all_sources.update(row.get("sources") or [])

    if metric.endswith("_percentage") and min_attempts:
        lines.extend(["", "### Note", f"Minimum attempts filter applied: {min_attempts}."])
    if metric in {"two_made", "two_point_shooting"}:
        lines.extend(
            [
                "",
                "### Short explanation",
                "Ranking uses total 2PT made, with attempts and percentage shown for context.",
            ]
        )
    if metric in {"three_made", "three_point_shooting"}:
        lines.extend(
            [
                "",
                "### Short explanation",
                "For best 3-point shooters, the ranking uses made threes first, then shows attempts and percentage for context.",
            ]
        )
    coach_explanation = coach_context_explanation(metric, str(metric_context) if metric_context else None)
    if coach_explanation:
        lines.extend(["", "### Short explanation", coach_explanation])

    if all_sources:
        lines.extend(["", "### Sources"])
        for source in sorted(all_sources):
            lines.append(f"- {source}")

    return {"answer": "\n".join(lines), "sources": sorted(all_sources)}


def rank_players(
    df: pd.DataFrame,
    metric: str,
    team: str | None,
    top_n: int = 3,
    metric_context: str | None = None,
    min_attempts: int = 0,
) -> dict[str, Any]:
    if df.empty:
        return {"answer": "I couldn't find box score data. Add FIBA Box Score PDFs and rebuild the extraction.", "sources": []}

    metric_key = SUPPORTED_METRICS.get(metric, metric)
    if metric_key == "three_point_shooting":
        rank_metric = "three_made"
    elif metric_key == "two_point_shooting":
        rank_metric = "two_made"
    else:
        rank_metric = metric_key
    scoped = df.copy()
    if team:
        scoped = scoped[scoped["team"].astype(str).str.upper() == team.upper()]
    if scoped.empty:
        return {"answer": "I couldn't find matching box score rows for that question.", "sources": []}
    if rank_metric not in scoped.columns:
        return {
            "answer": f"I couldn't find the {rank_metric} column in extracted box score data.",
            "sources": [],
        }

    metric_key_for_columns, made_col, attempted_col = metric_columns(metric_key)
    ranked = aggregate_rows(scoped, "player", rank_metric, "sum")
    if attempted_col and min_attempts:
        ranked = ranked[ranked[attempted_col] >= min_attempts]
    ranked = ranked.sort_values("rank_value", ascending=False).head(top_n)
    if ranked.empty:
        return {
            "answer": (
                "I found the metric, but no player met the minimum attempt filter "
                f"({min_attempts} attempts)."
            ),
            "sources": sorted(set(scoped["source_pdf"].dropna().astype(str))),
        }

    label = METRIC_LABELS.get(metric_key, metric_key)
    title_label = title_label_for_metric(metric_key_for_columns, metric_context)
    rebound_title = rebounder_title(rank_metric, team, len(ranked))
    title = rebound_title or (
        f"Top {team} player for {title_label}" if len(ranked) == 1 else f"Top {len(ranked)} {team or 'All'} players for {title_label}"
    )

    if metric_key in {"two_made", "two_point_shooting"}:
        ranking_basis = "Ranking uses total 2PT made, with attempts and percentage shown for context."
    else:
        ranking_basis = (
            f"Ranking uses {label} with a minimum of {min_attempts} attempts."
            if made_col and attempted_col and min_attempts
            else f"Ranking uses total {label}."
        )
    lines = ["### Direct answer", title, ranking_basis, "", "### Ranking"]
    sources: set[str] = set()
    for rank, (_, row) in enumerate(ranked.iterrows(), start=1):
        name = f"{row['player_name']} ({row['team']})"
        if metric_key == "two_point_shooting":
            value = (
                f"{int(row['two_made'])} made twos on {int(row['two_attempted'])} attempts "
                f"({format_percentage(row['two_made'], row['two_attempted'])})"
            )
        elif metric_key == "three_point_shooting":
            value = (
                f"{int(row['three_made'])} made threes on {int(row['three_attempted'])} attempts "
                f"({format_percentage(row['three_made'], row['three_attempted'])})"
            )
        elif made_col and attempted_col:
            value = f"{int(row[made_col])}/{int(row[attempted_col])} ({format_percentage(row[made_col], row[attempted_col])})"
        elif metric_key == "minutes_seconds":
            value = format_duration(row["rank_value"])
        else:
            value = format_number(row["rank_value"])
        lines.append(f"{rank}. **{name}** - {value}")
        for item in build_breakdown(scoped, row, "player", metric_key, "sum"):
            lines.append(f"   - {item}")
        sources.update(row.get("sources") or [])

    if metric_key == "two_point_shooting":
        lines.extend(
            [
                "",
                "### Short explanation",
                "Ranking uses total 2PT made, with attempts and percentage shown for context.",
            ]
        )
    if metric_key == "three_point_shooting":
        lines.extend(
            [
                "",
                "### Short explanation",
                "For 3PT shooting rankings, players are ordered by made threes first, with attempts and percentage shown for context.",
            ]
        )
    coach_explanation = coach_context_explanation(metric_key, metric_context)
    if coach_explanation:
        lines.extend(["", "### Short explanation", coach_explanation])

    if sources:
        lines.extend(["", "### Sources"])
        lines.extend(f"- {source}" for source in sorted(sources))
    return {"answer": "\n".join(lines), "sources": sorted(sources)}


def resolve_requested_players(requested_players: list[str], available_players: list[str]) -> list[str]:
    resolved: list[str] = []
    normalized_lookup = {normalize_text(player): player for player in available_players}
    for requested in requested_players:
        requested_norm = normalize_text(requested)
        if requested_norm in normalized_lookup:
            resolved.append(normalized_lookup[requested_norm])
            continue
        extracted = extract_player_names(requested, available_players, threshold=0.76)
        if extracted:
            resolved.append(extracted[0])
            continue
        best_player = None
        best_score = 0.0
        for player in available_players:
            score = SequenceMatcher(None, requested_norm, normalize_text(player)).ratio()
            if score > best_score:
                best_score = score
                best_player = player
        if best_player and best_score >= 0.72:
            resolved.append(best_player)
    return ordered_unique(resolved)


def aggregate_for_players(df: pd.DataFrame, players: list[str], team: str | None) -> tuple[pd.DataFrame, pd.DataFrame]:
    scoped = df.copy()
    if team:
        scoped = scoped[scoped["team"].astype(str).str.upper() == team.upper()]
    resolved_players = resolve_requested_players(players, available_players_from_df(scoped))
    filtered = scoped[scoped["player_name"].isin(resolved_players)].copy()
    if filtered.empty:
        return filtered, pd.DataFrame()
    grouped = filtered.groupby(["player_name", "team"], as_index=False)[COUNTING_COLUMNS + ["games_played"]].sum()
    grouped["three_percentage"] = safe_percentage(grouped["three_made"], grouped["three_attempted"])
    grouped["two_percentage"] = safe_percentage(grouped["two_made"], grouped["two_attempted"])
    grouped["field_goal_percentage"] = safe_percentage(grouped["fg_made"], grouped["fg_attempted"])
    grouped["free_throw_percentage"] = safe_percentage(grouped["ft_made"], grouped["ft_attempted"])
    return filtered, grouped


def metric_total_value(row: pd.Series, metric: str) -> str:
    metric_key = SUPPORTED_METRICS.get(metric, metric)
    if metric_key == "two_point_shooting":
        return f"{int(row['two_made'])}/{int(row['two_attempted'])} ({format_percentage(row['two_made'], row['two_attempted'])})"
    if metric_key == "three_point_shooting":
        return f"{int(row['three_made'])}/{int(row['three_attempted'])} ({format_percentage(row['three_made'], row['three_attempted'])})"
    if metric_key == "two_percentage":
        return f"{int(row['two_made'])}/{int(row['two_attempted'])} ({format_percentage(row['two_made'], row['two_attempted'])})"
    if metric_key == "field_goal_percentage":
        return f"{int(row['fg_made'])}/{int(row['fg_attempted'])} ({format_percentage(row['fg_made'], row['fg_attempted'])})"
    if metric_key == "free_throw_percentage":
        return f"{int(row['ft_made'])}/{int(row['ft_attempted'])} ({format_percentage(row['ft_made'], row['ft_attempted'])})"
    if metric_key == "minutes_seconds":
        return format_duration(row[metric_key])
    return format_number(row[metric_key])


def comparison_rank_value(row: pd.Series, metric: str) -> float:
    metric_key = SUPPORTED_METRICS.get(metric, metric)
    if metric_key == "two_point_shooting":
        attempted = float(row["two_attempted"])
        return (float(row["two_made"]) / attempted * 100) if attempted else 0.0
    if metric_key == "three_point_shooting":
        attempted = float(row["three_attempted"])
        return (float(row["three_made"]) / attempted * 100) if attempted else 0.0
    if metric_key == "two_percentage":
        attempted = float(row["two_attempted"])
        return (float(row["two_made"]) / attempted * 100) if attempted else 0.0
    if metric_key == "field_goal_percentage":
        attempted = float(row["fg_attempted"])
        return (float(row["fg_made"]) / attempted * 100) if attempted else 0.0
    if metric_key == "free_throw_percentage":
        attempted = float(row["ft_attempted"])
        return (float(row["ft_made"]) / attempted * 100) if attempted else 0.0
    return float(row[metric_key])


def compare_players(df: pd.DataFrame, players: list[str], metric: str, team: str | None = "EGY") -> dict[str, Any]:
    filtered, grouped = aggregate_for_players(df, players, team)
    if grouped.empty:
        return {"answer": "I couldn't find those players in the extracted box score CSV.", "sources": []}
    available = available_players_from_df(df[df["team"].astype(str).str.upper() == team.upper()] if team else df)
    requested_order = resolve_requested_players(players, available)
    order_lookup = {player: index for index, player in enumerate(requested_order)}
    grouped["_order"] = grouped["player_name"].map(order_lookup).fillna(999)
    grouped = grouped.sort_values("_order")

    metric_key = SUPPORTED_METRICS.get(metric, metric)
    title_label = TITLE_METRIC_LABELS.get(metric_key, metric_key)
    names = grouped["player_name"].tolist()
    title = f"{title_label} Comparison: {' vs '.join(names)}"
    lines = ["### " + title, ""]

    for _, row in grouped.iterrows():
        player = row["player_name"]
        lines.append(f"#### {player}")
        lines.append(f"- Total: {metric_total_value(row, metric_key)}")
        player_games = filtered[filtered["player_name"] == player].copy()
        for _, game in sorted_games(player_games).iterrows():
            lines.append(f"  - {game['match_name']}: {format_game_metric(game, metric_key)}")
        lines.append("")

    ranked = grouped.copy()
    ranked["_comparison_value"] = ranked.apply(lambda row: comparison_rank_value(row, metric_key), axis=1)
    ranked = ranked.sort_values("_comparison_value", ascending=False)
    best = ranked.iloc[0]
    lines.append("### Conclusion")
    if len(ranked) == 1:
        lines.append(f"{best['player_name']} is the only matched player for this comparison.")
    else:
        second = ranked.iloc[1]
        if metric_key == "two_point_shooting":
            lines.append(
                f"{best['player_name']} is better in 2PT shooting: {metric_total_value(best, metric_key)} "
                f"compared with {second['player_name']} at {metric_total_value(second, metric_key)}."
            )
        elif metric_key == "three_point_shooting":
            lines.append(
                f"{best['player_name']} is better in 3PT shooting: {metric_total_value(best, metric_key)} "
                f"compared with {second['player_name']} at {metric_total_value(second, metric_key)}."
            )
        else:
            lines.append(
                f"{best['player_name']} leads this comparison with {metric_total_value(best, metric_key)}, "
                f"ahead of {second['player_name']} at {metric_total_value(second, metric_key)}."
            )

    sources = sorted(set(filtered["source_pdf"].dropna().astype(str)))
    if sources:
        lines.extend(["", "### Sources"])
        lines.extend(f"- {source}" for source in sources)
    return {"answer": "\n".join(lines), "sources": sources}


def summarize_player(df: pd.DataFrame, player: str, team: str | None = "EGY") -> dict[str, Any]:
    filtered, grouped = aggregate_for_players(df, [player], team)
    if grouped.empty:
        return {"answer": "I couldn't find that player in the extracted box score CSV.", "sources": []}

    row = grouped.iloc[0]
    player_name = row["player_name"]
    lines = [
        f"### Player Summary: {player_name} ({row['team']})",
        "",
        f"- Games: {int(row['games_played'])}",
        f"- Points: {int(row['points'])}",
        f"- Rebounds: {int(row['total_rebounds'])} ({int(row['offensive_rebounds'])} OR, {int(row['defensive_rebounds'])} DR)",
        f"- Assists: {int(row['assists'])}",
        f"- Steals / Blocks: {int(row['steals'])} / {int(row['blocks'])}",
        f"- Turnovers: {int(row['turnovers'])}",
        f"- Plus/Minus: {int(row['plus_minus'])}",
        f"- Efficiency: {int(row['efficiency'])}",
        f"- 2PT: {int(row['two_made'])}/{int(row['two_attempted'])} ({format_percentage(row['two_made'], row['two_attempted'])})",
        f"- 3PT: {int(row['three_made'])}/{int(row['three_attempted'])} ({format_percentage(row['three_made'], row['three_attempted'])})",
        "",
        "### Match Breakdown",
    ]

    for _, game in sorted_games(filtered).iterrows():
        lines.append(
            f"- {game['match_name']}: {int(game['points'])} PTS, {int(game['total_rebounds'])} REB, "
            f"{int(game['assists'])} AST, {format_game_metric(game, 'two_point_shooting')} 2PT, "
            f"{format_game_metric(game, 'three_point_shooting')} 3PT, "
            f"{int(game['plus_minus'])} +/-"
        )

    sources = sorted(set(filtered["source_pdf"].dropna().astype(str)))
    if sources:
        lines.extend(["", "### Sources"])
        lines.extend(f"- {source}" for source in sources)
    return {"answer": "\n".join(lines), "sources": sources}


def normalized_metric(series: pd.Series) -> pd.Series:
    clean = pd.to_numeric(series, errors="coerce").fillna(0)
    minimum = clean.min()
    maximum = clean.max()
    if maximum == minimum:
        return pd.Series(0.5, index=clean.index)
    return (clean - minimum) / (maximum - minimum)


def recommend_more_minutes(
    df: pd.DataFrame,
    team: str | None = "EGY",
    top_n: int = 3,
    min_total_seconds: int = 180,
    min_games: int = 2,
) -> dict[str, Any]:
    if df.empty:
        return {"answer": "I couldn't find box score data. Add FIBA Box Score PDFs and rebuild the extraction.", "sources": []}

    normalized_team = normalise_team(team) or "EGY"
    scoped = df[df["team"].astype(str).str.upper() == normalized_team].copy()
    if scoped.empty:
        return {"answer": "I couldn't find matching box score rows for that team.", "sources": []}

    grouped = aggregate_rows(scoped, "player", "efficiency", "sum")
    eligible = grouped[
        (grouped["minutes_seconds"] >= int(min_total_seconds))
        | (grouped["games_played"] >= int(min_games))
    ].copy()
    if eligible.empty:
        eligible = grouped.copy()

    minutes = eligible["minutes_seconds"].replace(0, np.nan) / 60.0
    eligible["minutes_per_game"] = (eligible["minutes_seconds"] / eligible["games_played"].replace(0, np.nan) / 60.0).fillna(0)
    eligible["points_per_min"] = (eligible["points"] / minutes).replace([np.inf, -np.inf], 0).fillna(0)
    eligible["rebounds_per_min"] = (eligible["total_rebounds"] / minutes).replace([np.inf, -np.inf], 0).fillna(0)
    eligible["assists_per_min"] = (eligible["assists"] / minutes).replace([np.inf, -np.inf], 0).fillna(0)
    eligible["steals_per_min"] = (eligible["steals"] / minutes).replace([np.inf, -np.inf], 0).fillna(0)
    eligible["blocks_per_min"] = (eligible["blocks"] / minutes).replace([np.inf, -np.inf], 0).fillna(0)
    eligible["efficiency_per_min"] = (eligible["efficiency"] / minutes).replace([np.inf, -np.inf], 0).fillna(0)
    eligible["plus_minus_norm"] = normalized_metric(eligible["plus_minus"])

    eligible["impact_score"] = (
        eligible["efficiency_per_min"] * 3
        + eligible["points_per_min"] * 2
        + eligible["rebounds_per_min"] * 1.5
        + eligible["assists_per_min"] * 1.5
        + eligible["steals_per_min"] * 2
        + eligible["blocks_per_min"] * 2
        + eligible["plus_minus_norm"] * 0.4
    )
    eligible["opportunity_factor"] = 1 / (1 + eligible["minutes_per_game"] / 20)
    eligible["opportunity_score"] = eligible["impact_score"] * eligible["opportunity_factor"]

    recommended = eligible.sort_values(
        ["opportunity_score", "impact_score", "efficiency_per_min"],
        ascending=[False, False, False],
    ).head(max(1, int(top_n or 3)))

    lines = [
        "### Direct answer",
        f"{normalized_team} Players Who Deserve More Minutes",
        "These players are recommended because they produced positive impact relative to their current playing time, not because they already played the most minutes.",
        "",
        "### Opportunity Recommendations",
    ]

    for rank, (_, row) in enumerate(recommended.iterrows(), start=1):
        total_minutes = format_duration(row["minutes_seconds"])
        reason_bits = [
            f"{row['efficiency_per_min']:.2f} EFF/min",
            f"{row['points_per_min']:.2f} PTS/min",
        ]
        if row["rebounds_per_min"] > 0:
            reason_bits.append(f"{row['rebounds_per_min']:.2f} REB/min")
        if row["assists_per_min"] > 0:
            reason_bits.append(f"{row['assists_per_min']:.2f} AST/min")
        if row["steals_per_min"] > 0 or row["blocks_per_min"] > 0:
            reason_bits.append(f"{row['steals_per_min']:.2f} STL/min, {row['blocks_per_min']:.2f} BLK/min")

        lines.append(
            f"{rank}. **{row['player_name']}** - opportunity score {row['opportunity_score'] * 100:.1f}; "
            f"{row['minutes_per_game']:.1f} minutes/game, {total_minutes} total minutes"
        )
        lines.append(
            f"   - Production: {int(row['efficiency'])} EFF, {int(row['points'])} PTS, "
            f"{int(row['total_rebounds'])} REB, {int(row['assists'])} AST, "
            f"{int(row['steals'])} STL, {int(row['blocks'])} BLK, {int(row['plus_minus'])} +/-"
        )
        lines.append(
            "   - Why more minutes: productive per minute with "
            + ", ".join(reason_bits)
            + "."
        )

        player_games = scoped[scoped["player_name"] == row["player_name"]].copy()
        breakdown = []
        for _, game in sorted_games(player_games).iterrows():
            breakdown.append(
                f"{game['match_name']}: {format_duration(game['minutes_seconds'])}, "
                f"{int(game['points'])} PTS, {int(game['total_rebounds'])} REB, "
                f"{int(game['assists'])} AST, {int(game['steals'])} STL, "
                f"{int(game['efficiency'])} EFF"
            )
        if breakdown:
            lines.append("   - Match breakdown: " + "; ".join(breakdown))

    lines.extend(
        [
            "",
            "### Method",
            f"- Eligibility filter: at least {format_duration(min_total_seconds)} total minutes or {min_games} games played.",
            "- Opportunity score combines efficiency, points, rebounds, assists, steals, blocks, and plus/minus per minute, then favors players with lower current minutes per game.",
            "- This is a data-backed shortlist; final minute allocation should still consider roles, matchups, fouls, health, and practice form.",
        ]
    )

    sources = sorted(set(scoped["source_pdf"].dropna().astype(str)))
    if sources:
        lines.extend(["", "### Sources"])
        lines.extend(f"- {source}" for source in sources)
    return {"answer": "\n".join(lines), "sources": sources}


def rank_weighted_score(
    df: pd.DataFrame,
    metrics: list[str],
    weights: dict[str, float] | None = None,
    team: str | None = "EGY",
    top_n: int = 3,
    title: str | None = None,
    explanation: str | None = None,
) -> dict[str, Any]:
    if df.empty:
        return {"answer": "I couldn't find box score data. Add FIBA Box Score PDFs and rebuild the extraction.", "sources": []}

    normalized_team = normalise_team(team) or "EGY"
    scoped = df[df["team"].astype(str).str.upper() == normalized_team].copy()
    if scoped.empty:
        return {"answer": "I couldn't find matching box score rows for that team.", "sources": []}

    usable_metrics: list[str] = []
    for metric in metrics:
        metric_key = SUPPORTED_METRICS.get(str(metric), str(metric))
        if metric_key in COUNTING_COLUMNS or metric_key in {
            "three_percentage",
            "two_percentage",
            "field_goal_percentage",
            "free_throw_percentage",
        }:
            usable_metrics.append(metric_key)
    usable_metrics = ordered_unique(usable_metrics)
    if not usable_metrics:
        return {"answer": "I can't answer that from the uploaded reports because the required data is not available: requested weighted metrics.", "sources": []}

    grouped = aggregate_rows(scoped, "player", usable_metrics[0], "sum")
    weights = weights or {metric: 1.0 for metric in usable_metrics}
    grouped["weighted_score"] = 0.0
    for metric in usable_metrics:
        weight = float(weights.get(metric, weights.get(metric.replace("_seconds", ""), 1.0)) or 1.0)
        if metric in grouped.columns:
            grouped["weighted_score"] += normalized_metric(grouped[metric]) * weight

    ranked = grouped.sort_values("weighted_score", ascending=False).head(max(1, int(top_n or 3)))
    metric_labels = [TITLE_METRIC_LABELS.get(metric, METRIC_LABELS.get(metric, metric)) for metric in usable_metrics]
    heading = title or f"Top {len(ranked)} {normalized_team} players for {' + '.join(metric_labels)}"
    lines = [
        "### Direct answer",
        heading,
        explanation or "Ranking uses a weighted score calculated from the extracted box-score CSV.",
        "",
        "### Ranking",
    ]

    for rank, (_, row) in enumerate(ranked.iterrows(), start=1):
        metric_text = ", ".join(f"{METRIC_LABELS.get(metric, metric)} {format_number(row[metric])}" for metric in usable_metrics)
        lines.append(f"{rank}. **{row['player_name']} ({row['team']})** - weighted score {row['weighted_score'] * 100:.1f}; {metric_text}")
        for item in build_breakdown(scoped, row, "player", usable_metrics[0], "sum"):
            lines.append(f"   - {item}")

    sources = sorted(set(scoped["source_pdf"].dropna().astype(str)))
    if sources:
        lines.extend(["", "### Sources"])
        lines.extend(f"- {source}" for source in sources)
    return {"answer": "\n".join(lines), "sources": sources}


def squad_weights(strategy: str) -> dict[str, float]:
    if strategy == "defensive":
        return {
            "points_pg": 0.10,
            "total_rebounds_pg": 0.18,
            "assists_pg": 0.10,
            "steals_pg": 0.24,
            "blocks_pg": 0.16,
            "plus_minus_pg": 0.14,
            "efficiency_pg": 0.16,
            "turnovers_pg": -0.08,
        }
    if strategy == "shooting":
        return {
            "points_pg": 0.22,
            "three_percentage": 0.20,
            "free_throw_percentage": 0.08,
            "assists_pg": 0.12,
            "plus_minus_pg": 0.14,
            "efficiency_pg": 0.18,
            "turnovers_pg": -0.06,
        }
    if strategy == "pace":
        return {
            "points_pg": 0.18,
            "assists_pg": 0.18,
            "steals_pg": 0.18,
            "total_rebounds_pg": 0.12,
            "plus_minus_pg": 0.14,
            "efficiency_pg": 0.16,
            "turnovers_pg": -0.06,
        }
    return {
        "points_pg": 0.18,
        "total_rebounds_pg": 0.14,
        "assists_pg": 0.14,
        "steals_pg": 0.12,
        "blocks_pg": 0.08,
        "plus_minus_pg": 0.14,
        "efficiency_pg": 0.18,
        "turnovers_pg": -0.08,
    }


def recommend_squad(
    df: pd.DataFrame,
    team: str | None = "EGY",
    top_n: int = 5,
    strategy: str = "balanced",
) -> dict[str, Any]:
    if df.empty:
        return {"answer": "I couldn't find box score data. Add FIBA Box Score PDFs and rebuild the extraction.", "sources": []}

    normalized_team = normalise_team(team) or "EGY"
    scoped = df[df["team"].astype(str).str.upper() == normalized_team].copy()
    if scoped.empty:
        return {"answer": "I couldn't find matching box score rows for that team.", "sources": []}

    strategy = strategy if strategy in {"balanced", "defensive", "shooting", "pace"} else "balanced"
    grouped = aggregate_rows(scoped, "player", "efficiency", "sum")
    for column in ["points", "total_rebounds", "assists", "steals", "blocks", "plus_minus", "efficiency", "turnovers"]:
        grouped[f"{column}_pg"] = grouped[column] / grouped["games_played"].replace(0, np.nan)
        grouped[f"{column}_pg"] = grouped[f"{column}_pg"].fillna(0)

    grouped["squad_score"] = 0.0
    for column, weight in squad_weights(strategy).items():
        if column in grouped.columns:
            grouped["squad_score"] += normalized_metric(grouped[column]) * weight

    recommended = grouped.sort_values(
        ["squad_score", "efficiency_pg", "plus_minus_pg"],
        ascending=[False, False, False],
    ).head(max(1, int(top_n or 5)))

    names = recommended["player_name"].tolist()
    strategy_label = strategy.title()
    lines = [
        "### Direct answer",
        f"Recommended {normalized_team} squad ({strategy_label}): {', '.join(names)}",
        "This recommendation is calculated from the extracted box-score CSV, using player production across the uploaded reports.",
        "",
        "### Recommended Squad",
    ]

    for rank, (_, row) in enumerate(recommended.iterrows(), start=1):
        lines.append(
            f"{rank}. **{row['player_name']}** - squad score {row['squad_score'] * 100:.1f}; "
            f"{row['points_pg']:.1f} PTS, {row['total_rebounds_pg']:.1f} REB, "
            f"{row['assists_pg']:.1f} AST, {row['steals_pg']:.1f} STL, "
            f"{row['plus_minus_pg']:.1f} +/-, {row['efficiency_pg']:.1f} EFF per game"
        )

    lines.extend(
        [
            "",
            "### Why this squad",
            f"- Strategy profile: {strategy_label}.",
            "- Balanced profiles value scoring, rebounding, playmaking, defensive events, plus/minus, and efficiency; turnovers are a penalty.",
            "- Use this as a data-backed shortlist, then adjust for matchups, health, and roles.",
        ]
    )

    sources = sorted(set(scoped["source_pdf"].dropna().astype(str)))
    if sources:
        lines.extend(["", "### Sources"])
        lines.extend(f"- {source}" for source in sources)
    return {"answer": "\n".join(lines), "sources": sources}
