from __future__ import annotations

import json
import tempfile
from pathlib import Path

from analytics import (
    answer_stat_query,
    available_players_from_df,
    compare_players,
    load_box_scores,
    parse_question,
    parse_stat_question,
    rank_players,
    recommend_more_minutes,
    recommend_squad,
    selected_analytics_function,
    summarize_player,
)
from llama_client import classify_question, extractive_rag_fallback
from memory_store import MemoryStore, rewrite_followup_question
from rag_engine import RagEngine, build_chroma_pdf_index, chroma_available


class StaticLlmClient:
    def __init__(self, responses: list[dict], available: bool = True) -> None:
        self.responses = responses
        self.available = available
        self.calls = 0

    def is_available(self) -> bool:
        return self.available

    def generate(self, *args, **kwargs) -> str:
        self.calls += 1
        if not self.responses:
            return "not json"
        response = self.responses[min(self.calls - 1, len(self.responses) - 1)]
        return json.dumps(response)


CASES = [
    ("give me the best 3 players in Steals", "steals", 3, "sum"),
    ("i want best stealers", "steals", 3, "sum"),
    ("i want STLs", "steals", 3, "sum"),
    ("top stealers", "steals", 3, "sum"),
    ("best STL players", "steals", 3, "sum"),
    ("who has the most steals?", "steals", 1, "sum"),
    (
        "We have hard game facing a team that have a good handling skills and we want the best players to catch the ball from them",
        "steals",
        3,
        "sum",
    ),
    ("we need players to take the ball away from them", "steals", 3, "sum"),
    ("best players to pressure ball handlers", "steals", 3, "sum"),
    ("who can disrupt their guards?", "steals", 3, "sum"),
    ("top defensive pressure players", "steals", 3, "sum"),
    ("force turnovers against a good ball handling team", "steals", 3, "sum"),
    ("free throw", "free_throw_percentage", 3, "sum"),
    ("best free throw shooters", "free_throw_percentage", 3, "sum"),
    ("top FT%", "free_throw_percentage", 3, "sum"),
    ("most free throws made", "ft_made", 1, "sum"),
    ("who attempted the most free throws?", "ft_attempted", 1, "sum"),
    ("give me the top 2 pointers", "two_point_shooting", 2, "sum"),
    ("2 pts", "two_point_shooting", 3, "sum"),
    ("top 2PT players", "two_point_shooting", 3, "sum"),
    ("best two-point shooters", "two_point_shooting", 3, "sum"),
    ("top 2 players in points", "points", 2, "sum"),
    ("Who has the most assists for Egypt?", "assists", 1, "sum"),
    ("top 3 assists", "assists", 3, "sum"),
    ("We are facing a great defensive team that needs top assisters", "assists", 3, "sum"),
    ("i want best assisters", "assists", 3, "sum"),
    ("top playmakers", "assists", 3, "sum"),
    ("i want ASTs", "assists", 3, "sum"),
    ("who has the most assists?", "assists", 1, "sum"),
    ("Who has the most steals?", "steals", 1, "sum"),
    ("Can you suggest best 3 pts shooters?", "three_point_shooting", 3, "sum"),
    ("Top 5 scorers", "points", 5, "sum"),
    ("Who has the best plus/minus?", "plus_minus", 3, "sum"),
    ("Who has the highest rebounds per game?", "rebounds", 3, "avg"),
    ("We are facing a great defensive team that needs top rebounders", "rebounds", 3, "sum"),
    ("Who are our top rebounders?", "rebounds", 3, "sum"),
    ("top 5 rebounders", "rebounds", 5, "sum"),
    ("best defensive rebounders", "defensive_rebounds", 3, "sum"),
    ("best offensive rebounders", "offensive_rebounds", 3, "sum"),
]


def main() -> None:
    df = load_box_scores()
    available_players = available_players_from_df(df, "EGY")

    for question, expected_metric, expected_top_n, expected_aggregation in CASES:
        parsed = parse_stat_question(question)
        analytics_function = selected_analytics_function(parsed["metric"])
        print(f"question: {question}")
        print(f"parsed intent: {parsed['type']}")
        print(f"selected metric: {parsed['metric']}")
        print(f"selected analytics function: {analytics_function}")
        print()

        assert parsed["type"] == "stat_query"
        assert parsed["metric"] == expected_metric
        assert parsed["top_n"] == expected_top_n
        assert parsed["aggregation"] == expected_aggregation
        assert parsed["route"] == "analytics"

    route_cases = [
        ("Who has the most assists for Egypt?", "stat_query", "analytics", "assists", [], 1),
        ("top 3 assists", "stat_query", "analytics", "assists", [], 3),
        ("We are facing a great defensive team that needs top assisters", "stat_query", "analytics", "assists", [], 3),
        ("i want best assisters", "stat_query", "analytics", "assists", [], 3),
        ("top playmakers", "stat_query", "analytics", "assists", [], 3),
        ("i want ASTs", "stat_query", "analytics", "assists", [], 3),
        ("who has the most assists?", "stat_query", "analytics", "assists", [], 1),
        ("i want best stealers", "stat_query", "analytics", "steals", [], 3),
        ("i want STLs", "stat_query", "analytics", "steals", [], 3),
        ("top stealers", "stat_query", "analytics", "steals", [], 3),
        ("best STL players", "stat_query", "analytics", "steals", [], 3),
        ("who has the most steals?", "stat_query", "analytics", "steals", [], 1),
        (
            "We have hard game facing a team that have a good handling skills and we want the best players to catch the ball from them",
            "stat_query",
            "analytics",
            "steals",
            [],
            3,
        ),
        ("we need players to take the ball away from them", "stat_query", "analytics", "steals", [], 3),
        ("best players to pressure ball handlers", "stat_query", "analytics", "steals", [], 3),
        ("who can disrupt their guards?", "stat_query", "analytics", "steals", [], 3),
        ("top defensive pressure players", "stat_query", "analytics", "steals", [], 3),
        ("force turnovers against a good ball handling team", "stat_query", "analytics", "steals", [], 3),
        ("free throw", "stat_query", "analytics", "free_throw_percentage", [], 3),
        ("best free throw shooters", "stat_query", "analytics", "free_throw_percentage", [], 3),
        ("top FT%", "stat_query", "analytics", "free_throw_percentage", [], 3),
        ("most free throws made", "stat_query", "analytics", "ft_made", [], 1),
        ("who attempted the most free throws?", "stat_query", "analytics", "ft_attempted", [], 1),
        ("give me the top 2 pointers", "stat_query", "analytics", "two_point_shooting", [], 2),
        ("2 pts", "stat_query", "analytics", "two_point_shooting", [], 3),
        ("top 2PT players", "stat_query", "analytics", "two_point_shooting", [], 3),
        ("best two-point shooters", "stat_query", "analytics", "two_point_shooting", [], 3),
        ("top 2 players in points", "stat_query", "analytics", "points", [], 2),
        ("Who has the most steals?", "stat_query", "analytics", "steals", [], 1),
        ("Top 5 scorers", "stat_query", "analytics", "points", [], 5),
        ("We are facing a great defensive team that needs top rebounders", "stat_query", "analytics", "rebounds", [], 3),
        ("Compare Ahmed Metwaly and Amr Abdelhalim in 3PT shooting.", "player_comparison", "analytics", "three_point_shooting", ["Ahmed Metwaly", "Amr Abdelhalim"], None),
        ("Ahmed Metwaly vs Amr Abdelhalim in threes", "player_comparison", "analytics", "three_point_shooting", ["Ahmed Metwaly", "Amr Abdelhalim"], None),
        ("Who is better between Ahmed Metwaly and Amr Abdelhalim in 3-point shooting?", "player_comparison", "analytics", "three_point_shooting", ["Ahmed Metwaly", "Amr Abdelhalim"], None),
        ("Compare Ehab Amin and Ahmed Metwaly in assists", "player_comparison", "analytics", "assists", ["Ehab Amin", "Ahmed Metwaly"], None),
        ("Compare Ahmed Metwali and Amr Abdelhalm in assists", "player_comparison", "analytics", "assists", ["Ahmed Metwaly", "Amr Abdelhalim"], None),
        ("Ahmed Metwaly vs Amr Abdelhalim in rebounds", "player_comparison", "analytics", "rebounds", ["Ahmed Metwaly", "Amr Abdelhalim"], None),
        ("Top 3 EGY players in steals", "stat_query", "analytics", "steals", [], 3),
        ("suggest the best squad for the next game", "squad_recommendation", "analytics", None, [], 5),
        ("Which Egypt players should get more minutes?", "player_opportunity_recommendation", "analytics", None, [], 3),
        (
            "In your opinion who of egypt players would you recommend that we give more minutes in the coming matches?",
            "stat_query",
            "analytics",
            "minutes",
            [],
            3,
        ),
        ("Show Ahmed Metwaly full stats", "player_summary", "analytics", None, ["Ahmed Metwaly"], None),
        ("Show Ahmd Metwaly full stats", "player_summary", "analytics", None, ["Ahmed Metwaly"], None),
        ("Summarize Egypt performance against Uganda", "general_pdf_question", "rag", None, [], None),
    ]
    for question, expected_type, expected_route, expected_metric, expected_players, expected_top_n in route_cases:
        parsed = parse_question(question, available_players, default_team="EGY")
        print(f"route case: {question}")
        print(f"type: {parsed.get('type')}")
        print(f"route: {parsed.get('route')}")
        print(f"metric: {parsed.get('metric')}")
        print(f"players: {parsed.get('players')}")
        print(f"top_n: {parsed.get('top_n')}")
        print()

        assert parsed["type"] == expected_type
        assert parsed["route"] == expected_route
        if expected_metric is not None:
            assert parsed["metric"] == expected_metric
        if expected_players:
            assert parsed["players"] == expected_players
        if expected_top_n is not None:
            assert parsed["top_n"] == expected_top_n

    steals_query = parse_stat_question("give me the best 3 players in Steals")
    steals_answer = answer_stat_query(steals_query)["answer"]
    assert "Top 3 EGY players for Steals" in steals_answer
    assert "3PT made" not in steals_answer

    stealers_query = parse_stat_question("i want best stealers")
    stealers_answer = answer_stat_query(stealers_query)["answer"]
    assert "Top 3 EGY players for Steals" in stealers_answer
    assert "3PT made" not in stealers_answer

    ball_pressure_query = parse_stat_question(
        "We have hard game facing a team that have a good handling skills and we want the best players to catch the ball from them"
    )
    assert ball_pressure_query["metric_context"] == "ball_pressure"
    ball_pressure_answer = answer_stat_query(ball_pressure_query)["answer"]
    assert "Top 3 EGY players for Ball Pressure / Steals" in ball_pressure_answer
    assert "best available ball-pressure options based on steals" in ball_pressure_answer
    assert "Turnovers" not in ball_pressure_answer.splitlines()[1]

    free_throw_query = parse_stat_question("free throw")
    assert free_throw_query["metric"] == "free_throw_percentage"
    assert free_throw_query["route"] == "analytics"
    assert free_throw_query["min_attempts"] == 2
    free_throw_answer = answer_stat_query(free_throw_query)["answer"]
    assert "Top 3 EGY players for FT%" in free_throw_answer
    assert "/" in free_throw_answer

    two_point_query = parse_stat_question("give me the top 2 pointers")
    two_point_answer = answer_stat_query(two_point_query)["answer"]
    assert two_point_query["metric"] == "two_point_shooting"
    assert two_point_query["top_n"] == 2
    assert "Top 2 EGY players for 2PT Shooting" in two_point_answer
    assert "Top 2 EGY players for Points" not in two_point_answer

    assists_query = parse_stat_question("Who has the most assists for Egypt?")
    assists_answer = answer_stat_query(assists_query)["answer"]
    assert "Top EGY player for Assists" in assists_answer
    assert "3PT made" not in assists_answer

    assisters_query = parse_stat_question("We are facing a great defensive team that needs top assisters")
    assisters_answer = answer_stat_query(assisters_query)["answer"]
    assert "Top 3 EGY players for Assists" in assisters_answer
    assert "3PT made" not in assisters_answer

    rebound_query = parse_stat_question("We are facing a great defensive team that needs top rebounders")
    rebound_answer = answer_stat_query(rebound_query)["answer"]
    assert "Top 3 EGY Rebounders" in rebound_answer
    assert "Defensive Rebounders" not in rebound_answer

    comparison = compare_players(
        df,
        ["Ahmed Metwaly", "Amr Abdelhalim"],
        "three_point_shooting",
        "EGY",
    )["answer"]
    assert "3PT Shooting Comparison: Ahmed Metwaly vs Amr Abdelhalim" in comparison
    assert "Ahmed Metwaly" in comparison
    assert "Amr Abdelhalim" in comparison
    assert "Ehab Amin" not in comparison

    assist_comparison = compare_players(df, ["Ehab Amin", "Ahmed Metwaly"], "assists", "EGY")["answer"]
    assert "Assists Comparison: Ehab Amin vs Ahmed Metwaly" in assist_comparison

    ranking = rank_players(df, "steals", "EGY", 3)["answer"]
    assert "Top 3 EGY players for Steals" in ranking

    two_point_ranking = rank_players(df, "two_point_shooting", "EGY", 2)["answer"]
    assert "Top 2 EGY players for 2PT Shooting" in two_point_ranking
    assert "Top 2 EGY players for Points" not in two_point_ranking

    summary = summarize_player(df, "Ahmed Metwaly", "EGY")["answer"]
    assert "Player Summary: Ahmed Metwaly" in summary

    squad_answer = recommend_squad(df, "EGY", 5, "balanced")["answer"]
    assert "Recommended EGY squad (Balanced)" in squad_answer

    opportunity_answer = recommend_more_minutes(df, "EGY", 3)["answer"]
    assert "EGY Players Who Deserve More Minutes" in opportunity_answer
    assert "Top 3 EGY players for Minutes" not in opportunity_answer

    llm_first_analytics_cases = [
        (
            "suggest the best squad for the next game",
            {"type": "squad_recommendation", "route": "analytics", "team": "EGY", "top_n": 5, "strategy": "balanced"},
            "squad_recommendation",
            "analytics",
            None,
        ),
        (
            "free throw",
            {"type": "stat_query", "route": "analytics", "metric": "free_throw_percentage", "team": "EGY", "top_n": 3, "min_attempts": 2},
            "stat_query",
            "analytics",
            "free_throw_percentage",
        ),
        (
            "i want best stealers",
            {"type": "stat_query", "route": "analytics", "metric": "steals", "team": "EGY", "top_n": 3},
            "stat_query",
            "analytics",
            "steals",
        ),
        (
            "top assisters",
            {"type": "stat_query", "route": "analytics", "metric": "assists", "team": "EGY", "top_n": 3},
            "stat_query",
            "analytics",
            "assists",
        ),
        (
            "give me the top 2 pointers",
            {"type": "stat_query", "route": "analytics", "metric": "two_point_shooting", "team": "EGY", "top_n": 2},
            "stat_query",
            "analytics",
            "two_point_shooting",
        ),
        (
            "Which Egypt players should get more minutes?",
            {
                "type": "player_opportunity_recommendation",
                "route": "analytics",
                "metric": None,
                "team": "EGY",
                "top_n": 3,
                "strategy": "underused_high_impact",
            },
            "player_opportunity_recommendation",
            "analytics",
            None,
        ),
        (
            "In your opinion who of egypt players would you recommend that we give more minutes in the coming matches?",
            {
                "type": "player_opportunity_recommendation",
                "route": "analytics",
                "metric": None,
                "team": "EGY",
                "top_n": 3,
                "strategy": "underused_high_impact",
            },
            "player_opportunity_recommendation",
            "analytics",
            None,
        ),
        (
            "Compare Ahmed Metwaly and Amr Abdelhalim in 3PT shooting",
            {
                "type": "player_comparison",
                "route": "analytics",
                "metric": "three_point_shooting",
                "players": ["Ahmed Metwaly", "Amr Abdelhalim"],
                "team": "EGY",
            },
            "player_comparison",
            "analytics",
            "three_point_shooting",
        ),
    ]
    for question, response, expected_type, expected_route, expected_metric in llm_first_analytics_cases:
        client = StaticLlmClient([response])
        parsed = classify_question(question, client, available_players, default_team="EGY")
        print(f"llm-first analytics case: {question}")
        print(f"type: {parsed.get('type')}")
        print(f"route: {parsed.get('route')}")
        print(f"metric: {parsed.get('metric')}")
        print(f"classification_source: {parsed.get('_classification_source')}")
        print(f"llm_calls: {client.calls}")
        print()

        assert parsed["type"] == expected_type
        assert parsed["route"] == expected_route
        assert parsed["_classification_source"] == "llm"
        assert client.calls == 1
        if expected_metric is not None:
            assert parsed["metric"] == expected_metric

    llm_cases = [
        (
            "Summarize Egypt performance against Uganda",
            {"type": "general_pdf_question", "route": "rag", "team": "EGY", "opponent": "UGA"},
            "general_pdf_question",
            "rag",
            None,
        ),
    ]
    for question, response, expected_type, expected_route, expected_metric in llm_cases:
        client = StaticLlmClient([response])
        parsed = classify_question(question, client, available_players, default_team="EGY")
        print(f"llm case: {question}")
        print(f"type: {parsed.get('type')}")
        print(f"route: {parsed.get('route')}")
        print(f"metric: {parsed.get('metric')}")
        print(f"classification_source: {parsed.get('_classification_source')}")
        print(f"llm_calls: {client.calls}")
        print()

        assert parsed["type"] == expected_type
        assert parsed["route"] == expected_route
        assert parsed["_classification_source"] == "llm"
        assert client.calls == 1
        if expected_metric is not None:
            assert parsed["metric"] == expected_metric

    fallback_squad = classify_question(
        "suggest the best squad for the next game",
        StaticLlmClient([], available=False),
        available_players,
        default_team="EGY",
    )
    assert fallback_squad["type"] == "squad_recommendation"
    assert fallback_squad["route"] == "analytics"
    assert fallback_squad["_classification_source"] == "fallback"

    llm_more_minutes = classify_question(
        "In your opinion who of egypt players would you recommend that we give more minutes in the coming matches?",
        StaticLlmClient(
            [
                {
                    "type": "player_opportunity_recommendation",
                    "route": "analytics",
                    "metric": None,
                    "team": "EGY",
                    "top_n": 3,
                    "strategy": "underused_high_impact",
                }
            ]
        ),
        available_players,
        default_team="EGY",
    )
    assert llm_more_minutes["type"] == "player_opportunity_recommendation"
    assert llm_more_minutes["route"] == "analytics"
    assert llm_more_minutes["metric"] is None
    assert llm_more_minutes["_classification_source"] == "llm"

    rewritten, used_memory = rewrite_followup_question(
        "what about assists?",
        [
            {
                "parsed_intent": {
                    "type": "player_comparison",
                    "players": ["Ahmed Metwaly", "Amr Abdelhalim"],
                    "metric": "three_point_shooting",
                }
            }
        ],
    )
    assert used_memory
    assert rewritten == "Compare Ahmed Metwaly and Amr Abdelhalim in assists"

    rewritten_ranking, used_ranking_memory = rewrite_followup_question(
        "and rebounds?",
        [
            {
                "parsed_intent": {
                    "type": "stat_query",
                    "team": "EGY",
                    "top_n": 3,
                    "metric": "steals",
                }
            }
        ],
    )
    assert used_ranking_memory
    assert rewritten_ranking == "Top 3 EGY players for rebounds"
    parsed_rewritten = parse_question(rewritten_ranking, available_players, default_team="EGY")
    assert parsed_rewritten["route"] == "analytics"
    assert parsed_rewritten["metric"] == "rebounds"

    with tempfile.TemporaryDirectory() as tmp_dir:
        store = MemoryStore(
            db_path=Path(tmp_dir) / "chat_history.db",
            chroma_dir=Path(tmp_dir) / "chroma_memory",
            enable_chroma=False,
        )
        parsed_memory = parse_question("Top 3 EGY players for steals", available_players, default_team="EGY")
        store.save_message(
            "session-a",
            "user",
            "Top 3 EGY players for steals",
            parsed_memory,
            parsed_memory.get("route"),
            parsed_memory.get("metric"),
            parsed_memory.get("players") or [],
        )
        store.save_message("session-a", "assistant", "Top 3 EGY players for Steals", parsed_memory, "analytics", "steals", [])
        loaded = store.load_messages("session-a")
        assert [message.role for message in loaded] == ["user", "assistant"]
        similar = store.retrieve_similar("session-a", "and rebounds?", top_k=3)
        assert isinstance(similar, list)

    chroma_built = build_chroma_pdf_index() if chroma_available() else False
    rag_engine = RagEngine()
    rag_chunks = rag_engine.retrieve("What happened in the Egypt vs Uganda game?", "general_pdf_question", top_k=6)
    if chroma_built:
        assert rag_engine.last_retrieval_engine == "chroma"
    else:
        assert rag_engine.last_retrieval_engine in {"tfidf", "none"}
    rag_fallback = extractive_rag_fallback("What happened in the Egypt vs Uganda game?", rag_chunks)
    assert "Egypt beat Uganda 91-52" in rag_fallback
    assert "Ollama did not respond" not in rag_fallback


if __name__ == "__main__":
    main()
