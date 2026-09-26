from __future__ import annotations

import os
import shutil
import tempfile
from pathlib import Path

from services.extraction_service import ExtractionService
from services.project_store import ProjectStore
from services.question_service import QuestionService


ROOT = Path(__file__).parent


class FakeGroqClient:
    def __init__(self, responses: list[dict] | None = None, configured: bool = True) -> None:
        self.responses = responses or []
        self.configured = configured
        self.generate_calls = 0
        self.json_calls = 0

    def is_configured(self) -> bool:
        return self.configured

    def generate_text(self, *args, **kwargs) -> str:
        self.generate_calls += 1
        return "Fake Groq answer from retrieved context."

    def generate_json(self, *args, **kwargs) -> dict | None:
        self.json_calls += 1
        if not self.responses:
            return None
        return self.responses[min(self.json_calls - 1, len(self.responses) - 1)]


def seed_project(store: ProjectStore, project_id: str) -> None:
    store.create_project(project_id)
    extracted = store.extracted_dir(project_id)
    shutil.copy2(ROOT / "extracted" / "players_box_scores.csv", extracted / "players_box_scores.csv")
    shutil.copy2(ROOT / "extracted" / "pdf_chunks.csv", extracted / "pdf_chunks.csv")


def ask_with_fake(store: ProjectStore, fake_groq: FakeGroqClient) -> QuestionService:
    return QuestionService(store=store, groq_client=fake_groq, enable_chroma_memory=False)


def main() -> None:
    previous_bypass = os.environ.get("FAST_ANALYTICS_BYPASS")
    os.environ["FAST_ANALYTICS_BYPASS"] = "0"
    try:
        with tempfile.TemporaryDirectory() as tmp:
            store = ProjectStore(Path(tmp) / "projects")
            seed_project(store, "demo")

            groq_first = FakeGroqClient(
                [
                    {
                        "type": "stat_query",
                        "route": "analytics",
                        "metric": "steals",
                        "team": "EGY",
                        "players": [],
                        "top_n": 3,
                        "aggregation": "sum",
                        "group_by": "player",
                        "min_attempts": 0,
                    }
                ]
            )
            analytics = ask_with_fake(store, groq_first).ask("demo", "top stealers", team="EGY", session_id="s1", debug=True)
            assert groq_first.json_calls == 1
            assert groq_first.generate_calls == 0
            assert analytics["route"] == "analytics"
            assert analytics["metric"] == "steals"
            assert analytics["classification_source"] == "groq_first"
            assert "Top 3 EGY players for Steals" in analytics["answer"]

            fallback_groq = FakeGroqClient(configured=False)
            fallback = ask_with_fake(store, fallback_groq).ask("demo", "top stealers", team="EGY", session_id="s2", debug=True)
            assert fallback_groq.json_calls == 0
            assert fallback["route"] == "analytics"
            assert fallback["metric"] == "steals"
            assert fallback["classification_source"] == "deterministic_fallback"

            two_pt_groq = FakeGroqClient(
                [
                    {
                        "type": "stat_query",
                        "route": "analytics",
                        "metric": "two_point_shooting",
                        "team": "EGY",
                        "players": [],
                        "top_n": 2,
                        "aggregation": "sum",
                        "group_by": "player",
                        "min_attempts": 0,
                    }
                ]
            )
            two_pt = ask_with_fake(store, two_pt_groq).ask("demo", "give me the top 2 pointers", team="EGY", session_id="s3", debug=True)
            assert two_pt["route"] == "analytics"
            assert two_pt["metric"] == "two_point_shooting"
            assert "Top 2 EGY players for 2PT Shooting" in two_pt["answer"]
            assert "Top 2 EGY players for Points" not in two_pt["answer"]

            followup_groq = FakeGroqClient(
                [
                    {
                        "type": "player_comparison",
                        "route": "analytics",
                        "metric": "three_point_shooting",
                        "team": "EGY",
                        "players": ["Ahmed Metwaly", "Amr Abdelhalim"],
                        "top_n": None,
                        "aggregation": "sum",
                        "group_by": "player",
                        "min_attempts": 0,
                    },
                    {
                        "type": "player_comparison",
                        "route": "analytics",
                        "metric": "steals",
                        "team": "EGY",
                        "players": ["Ahmed Metwaly", "Amr Abdelhalim"],
                        "top_n": None,
                        "aggregation": "sum",
                        "group_by": "player",
                        "min_attempts": 0,
                    },
                ]
            )
            followup_service = ask_with_fake(store, followup_groq)
            followup_service.ask(
                "demo",
                "Compare Ahmed Metwaly and Amr Abdelhalim in 3PT shooting",
                team="EGY",
                session_id="s4",
                debug=True,
            )
            followup = followup_service.ask("demo", "what about steals?", team="EGY", session_id="s4", debug=True)
            assert followup["type"] == "player_comparison"
            assert followup["metric"] == "steals"
            assert followup["players"] == ["Ahmed Metwaly", "Amr Abdelhalim"]
            assert followup["rewritten_question"] == "Compare Ahmed Metwaly and Amr Abdelhalim in steals"

            typo_groq = FakeGroqClient(
                [
                    {
                        "type": "player_comparison",
                        "route": "analytics",
                        "metric": "three_point_shooting",
                        "team": "EGY",
                        "players": ["Ahmed Metwaly", "Amr Abdelhalim"],
                    },
                    {
                        "type": "player_comparison",
                        "route": "analytics",
                        "metric": "steals",
                        "team": "EGY",
                        "players": ["Ahmed Metwaly", "Amr Abdelhalim"],
                    },
                ]
            )
            typo_service = ask_with_fake(store, typo_groq)
            typo_service.ask(
                "demo",
                "Compare Ahmed Metwaly and Amr Abdelhalim in 3PT shooting",
                team="EGY",
                session_id="s5",
            )
            typo = typo_service.ask("demo", "what about steels?", team="EGY", session_id="s5", debug=True)
            assert typo["type"] == "player_comparison"
            assert typo["metric"] == "steals"
            assert typo["players"] == ["Ahmed Metwaly", "Amr Abdelhalim"]

            clarification_groq = FakeGroqClient(
                [
                    {
                        "type": "player_comparison",
                        "route": "analytics",
                        "metric": "rebounds",
                        "team": "EGY",
                        "players": ["Omar Oraby"],
                    }
                ]
            )
            clarification = ask_with_fake(store, clarification_groq).ask(
                "demo",
                "compare anas osama and omar oraby in rebounds",
                team="EGY",
                session_id="s6",
                debug=True,
            )
            assert clarification["route"] == "clarification"
            assert clarification["type"] == "player_comparison_needs_clarification"
            assert clarification["classification_source"] == "clarification"
            assert "Top 3" not in clarification["answer"]
            assert "Anas Mahmoud" in clarification["answer"]

            opportunity_wrong_groq = FakeGroqClient(
                [
                    {
                        "type": "stat_query",
                        "route": "analytics",
                        "metric": "minutes",
                        "analytics_recipe": {
                            "name": "rank_by_metric",
                            "ranking_metric": "minutes",
                        },
                        "team": "EGY",
                        "players": [],
                        "top_n": 3,
                    }
                ]
            )
            opportunity = ask_with_fake(store, opportunity_wrong_groq).ask(
                "demo",
                "Which Egyptian players deserve more minutes in upcoming matches?",
                team="EGY",
                session_id="s10",
                debug=True,
            )
            assert opportunity_wrong_groq.json_calls == 1
            assert opportunity["type"] == "analytic_recommendation"
            assert opportunity["route"] == "analytics"
            assert opportunity["metric"] is None
            assert opportunity["strategy"] == "underused_high_impact"
            assert opportunity["analytics_recipe"]["name"] == "opportunity_score"
            assert opportunity["classification_source"] == "groq_first"
            assert "EGY Players Who Deserve More Minutes" in opportunity["answer"]
            assert "Top 3 EGY players for Minutes" not in opportunity["answer"]

            get_more_minutes = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "analytic_recommendation",
                            "route": "analytics",
                            "metric": None,
                            "metrics": ["efficiency", "points", "rebounds", "assists", "steals", "blocks", "minutes_seconds"],
                            "analytics_recipe": {
                                "name": "opportunity_score",
                                "ranking_metric": "opportunity_score",
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 3,
                            "strategy": "underused_high_impact",
                        }
                    ]
                ),
            ).ask("demo", "Who should get more minutes next game?", team="EGY", session_id="s11", debug=True)
            assert get_more_minutes["type"] == "analytic_recommendation"
            assert get_more_minutes["route"] == "analytics"

            underused = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "analytic_recommendation",
                            "route": "analytics",
                            "metric": None,
                            "analytics_recipe": {
                                "name": "opportunity_score",
                                "ranking_metric": "opportunity_score",
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 3,
                            "strategy": "underused_high_impact",
                        }
                    ]
                ),
            ).ask("demo", "Which players are underused but efficient?", team="EGY", session_id="s12", debug=True)
            assert underused["type"] == "analytic_recommendation"
            assert underused["metric"] is None

            most_minutes = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "stat_query",
                            "route": "analytics",
                            "metric": "minutes",
                            "analytics_recipe": {
                                "name": "rank_by_metric",
                                "ranking_metric": "minutes",
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 1,
                        }
                    ]
                ),
            ).ask("demo", "Who played the most minutes?", team="EGY", session_id="s13", debug=True)
            assert most_minutes["type"] == "stat_query"
            assert most_minutes["metric"] == "minutes"
            assert "Minutes" in most_minutes["answer"]

            top_minutes = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "stat_query",
                            "route": "analytics",
                            "metric": "minutes",
                            "analytics_recipe": {
                                "name": "rank_by_metric",
                                "ranking_metric": "minutes",
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 3,
                        }
                    ]
                ),
            ).ask("demo", "top players by minutes", team="EGY", session_id="s14", debug=True)
            assert top_minutes["type"] == "stat_query"
            assert top_minutes["metric"] == "minutes"

            bench_chances = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "analytic_recommendation",
                            "route": "analytics",
                            "metric": None,
                            "metrics": ["efficiency", "points", "minutes_seconds"],
                            "analytics_recipe": {
                                "name": "opportunity_score",
                                "ranking_metric": "opportunity_score",
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 3,
                        }
                    ]
                ),
            ).ask("demo", "Which bench players deserve more chances?", team="EGY", session_id="s15", debug=True)
            assert bench_chances["route"] == "analytics"
            assert bench_chances["analytics_recipe"]["name"] == "opportunity_score"
            assert "Top 3 EGY players for Minutes" not in bench_chances["answer"]

            pressure = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "analytic_recommendation",
                            "route": "analytics",
                            "metric": "steals",
                            "metrics": ["steals"],
                            "analytics_recipe": {
                                "name": "rank_by_metric",
                                "ranking_metric": "steals",
                                "metric_context": "ball_pressure",
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 3,
                        }
                    ]
                ),
            ).ask("demo", "We need players who can pressure their guards", team="EGY", session_id="s16", debug=True)
            assert pressure["route"] == "analytics"
            assert pressure["metric"] == "steals"
            assert "Steals" in pressure["answer"]

            paint = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "analytic_recommendation",
                            "route": "analytics",
                            "metric": None,
                            "metrics": ["blocks", "defensive_rebounds"],
                            "analytics_recipe": {
                                "name": "weighted_score",
                                "metrics": ["blocks", "defensive_rebounds"],
                                "weights": {"blocks": 2, "defensive_rebounds": 1},
                                "explanation": "Use blocks and defensive rebounds for paint protection.",
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 3,
                        }
                    ]
                ),
            ).ask("demo", "Who can protect the paint?", team="EGY", session_id="s17", debug=True)
            assert paint["route"] == "analytics"
            assert paint["analytics_recipe"]["name"] == "weighted_score"
            assert "weighted score" in paint["answer"]

            creators = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "analytic_recommendation",
                            "route": "analytics",
                            "metric": "assists",
                            "metrics": ["assists", "turnovers"],
                            "analytics_recipe": {
                                "name": "rank_by_metric",
                                "ranking_metric": "assists",
                                "show_context": ["turnovers"],
                            },
                            "team": "EGY",
                            "players": [],
                            "top_n": 3,
                        }
                    ]
                ),
            ).ask("demo", "Who are our best creators?", team="EGY", session_id="s18", debug=True)
            assert creators["route"] == "analytics"
            assert creators["metric"] == "assists"

            ambiguous_shooters = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "clarification",
                            "route": "clarification",
                            "metric": None,
                            "metrics": [],
                            "analytics_recipe": {"name": "unsupported"},
                            "clarification_question": "Do you mean best 3PT shooters, 2PT shooters, free-throw shooters, or overall FG%?",
                            "team": "EGY",
                            "players": [],
                            "top_n": None,
                        }
                    ]
                ),
            ).ask("demo", "Who are our best shooters?", team="EGY", session_id="s19", debug=True)
            assert ambiguous_shooters["route"] == "clarification"
            assert "3PT" in ambiguous_shooters["answer"]

            unsupported = ask_with_fake(
                store,
                FakeGroqClient(
                    [
                        {
                            "type": "unsupported",
                            "route": "unsupported",
                            "metric": None,
                            "metrics": [],
                            "analytics_recipe": {
                                "name": "unsupported",
                                "missing_data": "heart rate",
                            },
                            "reason": "Heart rate data is not available in the uploaded reports.",
                            "team": "EGY",
                            "players": [],
                            "top_n": None,
                        }
                    ]
                ),
            ).ask("demo", "Who has the best heart rate?", team="EGY", session_id="s20", debug=True)
            assert unsupported["route"] == "unsupported"
            assert "heart rate" in unsupported["answer"].lower()

            rag_groq = FakeGroqClient(
                [
                    {
                        "type": "general_pdf_question",
                        "route": "rag",
                        "metric": None,
                        "team": "EGY",
                        "players": [],
                        "top_n": None,
                    }
                ]
            )
            rag = ask_with_fake(store, rag_groq).ask(
                "demo",
                "Summarize Egypt performance against Uganda",
                team="EGY",
                session_id="s7",
                debug=True,
            )
            assert rag_groq.json_calls == 1
            assert rag_groq.generate_calls == 1
            assert rag["route"] == "rag"
            assert rag["classification_source"] == "groq_first"
            assert rag["retrieval_engine"] in {"chroma", "tfidf", "none"}
            assert rag["sources"]

            os.environ["FAST_ANALYTICS_BYPASS"] = "1"
            bypass_groq = FakeGroqClient(
                [
                    {
                        "type": "stat_query",
                        "route": "analytics",
                        "metric": "steals",
                        "team": "EGY",
                    }
                ]
            )
            bypass = ask_with_fake(store, bypass_groq).ask("demo", "top stealers", team="EGY", session_id="s8", debug=True)
            assert bypass_groq.json_calls == 0
            assert bypass["route"] == "analytics"
            assert bypass["metric"] == "steals"
            assert bypass["classification_source"] == "deterministic_fallback"
            os.environ["FAST_ANALYTICS_BYPASS"] = "0"

            missing_key_rag = ask_with_fake(store, FakeGroqClient(configured=False)).ask(
                "demo",
                "Summarize Egypt performance against Uganda",
                team="EGY",
                session_id="s9",
            )
            assert missing_key_rag["route"] == "rag"
            assert isinstance(missing_key_rag["answer"], str)

            rebuild_store = ProjectStore(Path(tmp) / "rebuild-projects")
            extraction = ExtractionService(rebuild_store)
            rebuild_store.create_project("rebuild-demo")
            sample_pdf = next((ROOT / "pdfs").glob("FIBA Box Score UGA*.pdf"))
            shutil.copy2(sample_pdf, rebuild_store.pdf_dir("rebuild-demo") / sample_pdf.name)

            import rag_engine

            original_build_chroma = rag_engine.build_chroma_pdf_index
            rag_engine.build_chroma_pdf_index = lambda *args, **kwargs: False
            try:
                summary = extraction.rebuild("rebuild-demo")
            finally:
                rag_engine.build_chroma_pdf_index = original_build_chroma

            assert summary["status"] == "ready"
            assert summary["pdf_count"] == 1
            assert summary["player_rows"] > 0
            assert summary["chunk_rows"] > 0
            assert rebuild_store.box_score_csv("rebuild-demo").exists()
            assert rebuild_store.chunks_csv("rebuild-demo").exists()
            assert rebuild_store.status("rebuild-demo")["status"] == "ready"
    finally:
        if previous_bypass is None:
            os.environ.pop("FAST_ANALYTICS_BYPASS", None)
        else:
            os.environ["FAST_ANALYTICS_BYPASS"] = previous_bypass


if __name__ == "__main__":
    main()
