from __future__ import annotations

import json
import os
import re
from typing import Any

import requests

from analytics import SUPPORTED_METRICS, detect_stat_metric, parse_question, parse_stat_question
from rag_engine import RetrievedChunk, chunks_to_context
from services.groq_client import GroqClient
from language_utils import classifier_language_instruction, response_language_instruction


NOT_FOUND_MESSAGE = "I couldn't find this information in the uploaded PDF reports."


class OllamaClient:
    """Backward-compatible name for the old Streamlit code; requests now go to Groq."""

    def __init__(
        self,
        model: str | None = None,
        base_url: str | None = None,
        timeout: int | None = None,
    ) -> None:
        self.model = model or os.getenv("GROQ_MODEL", "llama-3.1-8b-instant")
        self.base_url = (base_url or os.getenv("GROQ_BASE_URL", "https://api.groq.com/openai/v1")).rstrip("/")
        self.timeout = timeout or int(os.getenv("GROQ_TIMEOUT", "60"))
        self._client = GroqClient(model=self.model, base_url=self.base_url, timeout=self.timeout)

    def is_available(self) -> bool:
        return self._client.is_configured()

    def generate(
        self,
        prompt: str,
        system: str = "",
        temperature: float = 0.1,
        json_mode: bool = False,
        num_predict: int = 512,
    ) -> str:
        response_format = {"type": "json_object"} if json_mode else None
        return self._client.generate_text(
            prompt=prompt,
            system=system,
            temperature=temperature,
            max_tokens=num_predict,
            response_format=response_format,
        )


def extract_json_object(text: str) -> dict[str, Any] | None:
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass
    match = re.search(r"\{.*\}", text, flags=re.DOTALL)
    if not match:
        return None
    try:
        return json.loads(match.group(0))
    except json.JSONDecodeError:
        return None


def chunk_sources(chunks: list[RetrievedChunk]) -> list[str]:
    return sorted({f"{chunk.source_pdf} p.{chunk.page_number}" for chunk in chunks})


def extract_score_line(text: str) -> tuple[str, int, int, str] | None:
    match = re.search(
        r"\b([A-Z][A-Za-z .'-]+?)\s+(\d{2,3})\s+-\s+(\d{2,3})\s+([A-Z][A-Za-z .'-]+?)\b",
        text,
    )
    if not match:
        return None
    home = " ".join(match.group(1).split())
    away = " ".join(match.group(4).split())
    return home, int(match.group(2)), int(match.group(3)), away


def extract_quarter_scores(text: str) -> list[tuple[int, int]]:
    match = re.search(r"\((\d{1,3}-\d{1,3}(?:,\s*\d{1,3}-\d{1,3})+)\)", text)
    if not match:
        return []
    quarters: list[tuple[int, int]] = []
    for item in match.group(1).split(","):
        left, right = item.strip().split("-", 1)
        quarters.append((int(left), int(right)))
    return quarters


def extractive_rag_fallback(question: str, chunks: list[RetrievedChunk]) -> str:
    if not chunks:
        return NOT_FOUND_MESSAGE

    context = " ".join(chunk.text for chunk in chunks)
    score = extract_score_line(context)
    quarters = extract_quarter_scores(context)
    sources = chunk_sources(chunks)

    lines: list[str] = ["### Direct answer"]
    if score:
        home, home_score, away_score, away = score
        winner = home if home_score > away_score else away
        loser = away if home_score > away_score else home
        winner_score = max(home_score, away_score)
        loser_score = min(home_score, away_score)
        lines.append(f"{winner} beat {loser} {winner_score}-{loser_score}.")
    else:
        lines.append("I found relevant PDF context, but could not extract a clean final score automatically.")

    if quarters:
        lines.extend(["", "### Game Flow"])
        if score:
            home, _, _, away = score
            quarter_text = ", ".join(f"Q{index + 1} {home} {left}-{right} {away}" for index, (left, right) in enumerate(quarters))
            lines.append(f"- Quarter scores: {quarter_text}.")
            home_won = sum(1 for left, right in quarters if left > right)
            away_won = sum(1 for left, right in quarters if right > left)
            if away_won == len(quarters):
                lines.append(f"- {away} outscored {home} in every quarter.")
            elif home_won == len(quarters):
                lines.append(f"- {home} outscored {away} in every quarter.")
        else:
            lines.append("- Quarter scores: " + ", ".join(f"{left}-{right}" for left, right in quarters) + ".")

    report_types = sorted({chunk.report_type for chunk in chunks if chunk.report_type})
    if report_types:
        lines.extend(["", "### Retrieved Reports"])
        lines.append("- " + ", ".join(report_types))

    if sources:
        lines.extend(["", "### Sources"])
        lines.extend(f"- {source}" for source in sources)

    return "\n".join(lines)


ANALYTICS_INTENT_TYPES = {
    "stat_query",
    "player_comparison",
    "player_summary",
    "squad_recommendation",
    "analytic_recommendation",
    "player_opportunity_recommendation",
}
RAG_INTENT_TYPES = {"general_pdf_question", "lineup_question", "play_by_play_question", "plus_minus_question"}
ALLOWED_INTENT_TYPES = ANALYTICS_INTENT_TYPES | RAG_INTENT_TYPES


def int_or_default(value: Any, default: int) -> int:
    try:
        return max(1, int(value))
    except (TypeError, ValueError):
        return default


def list_or_empty(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []
    return [str(item).strip() for item in value if str(item).strip()]


def fallback_question_type(
    question: str,
    default_team: str,
    available_players: list[str] | None = None,
) -> dict[str, Any]:
    lowered = question.lower()
    parsed = parse_question(question, available_players=available_players or [], default_team=default_team)
    if parsed.get("route") == "analytics":
        parsed["_classification_source"] = "fallback"
        return parsed
    if "line" in lowered and "up" in lowered:
        return {"type": "lineup_question", "route": "rag", "_classification_source": "fallback"}
    if "play by play" in lowered or "possession" in lowered or "quarter" in lowered:
        return {"type": "play_by_play_question", "route": "rag", "_classification_source": "fallback"}
    if looks_like_stat_question(question):
        parsed = parse_stat_question(question, default_team=default_team)
        parsed["_classification_source"] = "fallback"
        return parsed
    if "plus" in lowered or "+/-" in lowered:
        parsed = parse_stat_question(question, default_team=default_team)
        parsed["_classification_source"] = "fallback"
        return parsed
    return {"type": "general_pdf_question", "route": "rag", "_classification_source": "fallback"}


def looks_like_stat_question(question: str) -> bool:
    return detect_stat_metric(question) is not None


def normalize_llm_intent(
    intent: dict[str, Any],
    question: str,
    available_players: list[str] | None,
    default_team: str,
) -> dict[str, Any]:
    fallback = fallback_question_type(question, default_team, available_players=available_players)
    intent_type = str(intent.get("type") or "").strip()
    if intent_type not in ALLOWED_INTENT_TYPES:
        return fallback

    deterministic_metric = detect_stat_metric(question)
    team = intent.get("team") or fallback.get("team") or default_team

    if intent_type == "stat_query":
        base = parse_stat_question(question, default_team=str(team or default_team))
        metric = deterministic_metric or intent.get("metric") or base.get("metric")
        metric = str(metric or "points")
        if metric not in SUPPORTED_METRICS:
            return fallback
        base["metric"] = metric
        base["route"] = "analytics"
        base["team"] = team or base.get("team") or default_team
        base["top_n"] = int_or_default(intent.get("top_n"), int_or_default(base.get("top_n"), 3))
        base["aggregation"] = str(intent.get("aggregation") or base.get("aggregation") or "sum")
        base["min_attempts"] = int_or_default(intent.get("min_attempts"), int_or_default(base.get("min_attempts"), 0))
        if metric == "free_throw_percentage" and base["min_attempts"] == 0:
            base["min_attempts"] = 2
        base["group_by"] = intent.get("group_by") or base.get("group_by") or "player"
        base["players"] = list_or_empty(intent.get("players")) or fallback.get("players", [])
        base["_classification_source"] = "llm"
        return base

    if intent_type == "player_comparison":
        players = list_or_empty(intent.get("players")) or fallback.get("players", [])
        if len(players) < 2:
            return fallback
        metric = deterministic_metric or intent.get("metric") or fallback.get("metric") or "points"
        metric = str(metric)
        if metric not in SUPPORTED_METRICS:
            metric = "points"
        return {
            "type": "player_comparison",
            "route": "analytics",
            "metric": metric,
            "players": players,
            "team": team or default_team,
            "top_n": None,
            "_classification_source": "llm",
        }

    if intent_type == "player_summary":
        players = list_or_empty(intent.get("players")) or fallback.get("players", [])
        if not players:
            return fallback
        return {
            "type": "player_summary",
            "route": "analytics",
            "metric": deterministic_metric or intent.get("metric"),
            "players": players[:1],
            "team": team or default_team,
            "top_n": None,
            "_classification_source": "llm",
        }

    if intent_type == "squad_recommendation":
        strategy = str(intent.get("strategy") or fallback.get("strategy") or "balanced").lower()
        if strategy not in {"balanced", "defensive", "shooting", "pace"}:
            strategy = "balanced"
        return {
            "type": "squad_recommendation",
            "route": "analytics",
            "team": team or default_team,
            "top_n": int_or_default(intent.get("top_n"), int_or_default(fallback.get("top_n"), 5)),
            "strategy": strategy,
            "_classification_source": "llm",
        }

    if intent_type in {"analytic_recommendation", "player_opportunity_recommendation"}:
        return {
            "type": intent_type,
            "route": "analytics",
            "metric": None,
            "players": list_or_empty(intent.get("players")) or fallback.get("players", []),
            "team": team or default_team,
            "top_n": int_or_default(intent.get("top_n"), int_or_default(fallback.get("top_n"), 3)),
            "strategy": str(intent.get("strategy") or fallback.get("strategy") or "underused_high_impact"),
            "_classification_source": "llm",
        }

    if fallback.get("route") == "analytics":
        return fallback
    return {
        "type": intent_type,
        "route": "rag",
        "team": team or default_team,
        "opponent": intent.get("opponent"),
        "metric": None,
        "players": [],
        "top_n": None,
        "_classification_source": "llm",
    }


def classify_question(
    question: str,
    client: OllamaClient,
    available_players: list[str] | None = None,
    default_team: str = "EGY",
) -> dict[str, Any]:
    deterministic = fallback_question_type(question, default_team, available_players=available_players)
    if os.getenv("FAST_ANALYTICS_BYPASS", "0").lower() in {"1", "true", "yes", "on"} and deterministic.get("route") == "analytics":
        return deterministic

    system = (
        "You classify basketball coach questions about uploaded FIBA PDF reports. "
        "Return JSON only. Do not calculate statistics or answer the question. "
        f"{classifier_language_instruction()}"
    )
    player_hint = ", ".join((available_players or [])[:80])
    prompt = f"""
Return one JSON object only.

Classify this question into exactly one type:
- stat_query
- player_comparison
- player_summary
- squad_recommendation
- player_opportunity_recommendation
- general_pdf_question
- play_by_play_question
- lineup_question
- plus_minus_question

Return this shape, keeping irrelevant fields null or empty:
{{
  "type": "stat_query",
  "route": "analytics",
  "metric": "points",
  "team": "{default_team}",
  "opponent": null,
  "players": [],
  "top_n": 3,
  "aggregation": "sum",
  "min_attempts": 0,
  "group_by": "player",
  "strategy": null
}}

Supported metrics:
points, rebounds, defensive_rebounds, offensive_rebounds, assists, steals, blocks, turnovers, plus_minus, efficiency,
minutes, two_point_shooting, two_made, two_attempted, two_percentage,
three_point_shooting, three_made, three_attempted, three_percentage, field_goal_percentage,
free_throw_percentage, ft_made, ft_attempted.

Available player names for matching player questions:
{player_hint}

Rules:
- Groq only classifies intent. Pandas will calculate all numbers later.
- Statistical, comparison, player summary, and squad recommendation questions must use route analytics.
- General report, lineup report, play-by-play, and non-computable context questions use route rag.
- Rebound/rebounds/rebounder/rebounders/reb/boards means metric rebounds and route analytics.
- Defensive rebounds/defensive rebounders/dreb means metric defensive_rebounds. The phrase "defensive team" alone does not mean defensive_rebounds.
- Offensive rebounds/offensive rebounders/oreb means metric offensive_rebounds.
- Free throw/free throws/FT/foul shot/charity stripe means metric free_throw_percentage by default with min_attempts 2.
- Most free throws made means metric ft_made. Most free throws attempted means metric ft_attempted.
- Only classify 2PT when explicit shooting language appears: 2pt, 2 pts, 2-point, two point, two-pointer, or 2 pointers.
- The number in "top 3" or "best 3 players" is only top_n. It is not a 3PT clue.
- Only classify 3PT when explicit shooting language appears: 3pt, 3 pts, 3-point, three point, three-pointer, threes, or 3 pointers.
- Steals/stl has higher priority than 3PT.
- "best 3 pts shooters" means metric three_made, not three_percentage. Rank by made threes.
- "best percentage" means a percentage metric and min_attempts should be at least 5 unless the user gives a number.
- "per game" or "average" means aggregation avg.
- If comparing teams, set group_by to "team" and team to null unless one team is named.
- For player rankings with no named team, default team is {default_team}.
- If the user asks to compare players, choose player_comparison and include players from the available list when possible.
- If the user asks about one player's stats/performance, choose player_summary.
- If the user asks for best squad, starting five, next-game squad, or who should start, choose squad_recommendation with top_n 5 and strategy balanced unless the question implies defensive, shooting, or pace.
- If the user asks which players deserve more minutes, should get more minutes, are underused, need more playing time, or should get more opportunities, choose player_opportunity_recommendation with route analytics, metric null, top_n 3, and strategy underused_high_impact.
- If the question asks about lineups, choose lineup_question.
- If it asks about play-by-play events, choose play_by_play_question.
- If it asks generally about a plus/minus report and is not a ranking/stat calculation, choose plus_minus_question.

Examples:
Question: suggest the best squad for the next game
JSON: {{"type":"squad_recommendation","route":"analytics","team":"{default_team}","top_n":5,"strategy":"balanced"}}

Question: Which Egypt players should get more minutes?
JSON: {{"type":"player_opportunity_recommendation","route":"analytics","metric":null,"team":"EGY","top_n":3,"strategy":"underused_high_impact"}}

Question: free throw
JSON: {{"type":"stat_query","route":"analytics","metric":"free_throw_percentage","team":"{default_team}","top_n":3,"min_attempts":2,"aggregation":"sum","group_by":"player"}}

Question: Compare Ahmed Metwaly and Amr Abdelhalim in 3PT shooting
JSON: {{"type":"player_comparison","route":"analytics","metric":"three_point_shooting","players":["Ahmed Metwaly","Amr Abdelhalim"],"team":"{default_team}"}}

Question: Summarize Egypt performance against Uganda
JSON: {{"type":"general_pdf_question","route":"rag","team":"EGY","opponent":"UGA"}}

Question: {question}
"""
    if not client.is_available():
        return deterministic

    try:
        raw = client.generate(prompt, system=system, temperature=0, json_mode=True, num_predict=260)
        parsed = extract_json_object(raw)
    except (requests.RequestException, ValueError):
        parsed = None

    if not parsed:
        return deterministic

    normalized = normalize_llm_intent(parsed, question, available_players, default_team)
    if normalized.get("route") == "analytics":
        normalized_type = str(normalized.get("type") or "")
        if normalized_type in {"analytic_recommendation", "player_opportunity_recommendation"}:
            return normalized
        if deterministic.get("type") == "player_opportunity_recommendation" and normalized_type == "stat_query":
            return deterministic
    return normalized


def format_analytics_answer(question: str, analytics_answer: str, client: OllamaClient) -> str:
    if not analytics_answer.strip() or not client.is_available():
        return analytics_answer

    system = (
        "You are a professional basketball analyst polishing an answer for a coach. "
        "The draft answer contains correct statistics. Format it into a clear, concise sentence or list. "
        "Do not add any numbers or claims that are not in the draft. "
        f"{response_language_instruction(question)}"
    )
    prompt = f"""
Question:
{question}

Pandas analytics draft:
{analytics_answer}

Rewrite the draft clearly with:
- a direct answer first
- rankings or bullets where relevant
- a short coach explanation
- sources preserved

If the draft already looks clear, keep it mostly unchanged.
"""
    try:
        format_client = OllamaClient(model=client.model, base_url=client.base_url, timeout=min(client.timeout, 30))
        formatted = format_client.generate(prompt, system=system, temperature=0.1, json_mode=False, num_predict=700)
    except requests.RequestException:
        return analytics_answer

    return formatted if formatted.strip() else analytics_answer


def answer_from_chunks(question: str, chunks: list[RetrievedChunk], client: OllamaClient) -> str:
    if not chunks:
        return NOT_FOUND_MESSAGE

    context = chunks_to_context(chunks)
    system = (
        "You are a professional basketball assistant for a coach. Answer the question using ONLY the provided text extracts. "
        "If the answer is not in the text, say exactly: 'I couldn't find this information in the uploaded PDF reports.' "
        "Do not guess or add outside information. "
        f"{response_language_instruction(question)}"
    )
    prompt = f"""
Use only these retrieved PDF chunks to answer the coach's question.
Start with a direct answer, then add concise bullets or ranking when useful.
Include source PDF names and page numbers used.
Do not invent numbers, names, events, or conclusions.

Question:
{question}

Retrieved PDF context:
{context}
"""
    if not client.is_available():
        return extractive_rag_fallback(question, chunks)

    try:
        return client.generate(prompt, system=system, temperature=0.1, num_predict=800)
    except requests.RequestException:
        return extractive_rag_fallback(question, chunks)


def polish_stat_answer(question: str, numeric_answer: str, client: OllamaClient) -> str:
    if not client.is_available():
        return numeric_answer

    system = (
        "You are a professional basketball analyst. Rewrite the supplied numeric answer clearly for a coach. "
        "Do not add, remove, or change any numbers, names, rankings, or sources. "
        f"{response_language_instruction(question)}"
    )
    prompt = f"""
Coach question:
{question}

Numeric answer to preserve exactly:
{numeric_answer}

Return a clear Markdown answer with the same facts.
"""
    try:
        return client.generate(prompt, system=system, temperature=0.05, num_predict=700)
    except requests.RequestException:
        return numeric_answer
