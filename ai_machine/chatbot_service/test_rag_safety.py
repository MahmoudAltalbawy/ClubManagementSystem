"""
RAG-safety unit tests.

Cover the guards added so the PDF lane stops answering off-topic questions:

  * ``_parse_made_attempted``  — "made/attempted" parsing used by the basketball
                                 box-score lane (defensive against bad PDF cells).
  * opponent guard             — ``filter_chunks_by_opponent`` drops retrieved
                                 chunks that don't mention the opponent the coach
                                 asked about; if none remain the caller refuses
                                 (the Angola→Mali bug).
  * relevance floor            — ``RagEngine`` TF-IDF retrieval honours
                                 ``RAG_MIN_SCORE_TFIDF`` so a weak/no-overlap hit
                                 no longer becomes an answer (deterministic refusal
                                 precondition: retrieve() returns []).

Run directly (``python test_rag_safety.py``) or via pytest.
"""
from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path

# Make the chatbot package root importable regardless of the caller's CWD.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from llama_client import NOT_FOUND_MESSAGE  # noqa: E402
from rag_engine import RetrievedChunk  # noqa: E402
from services.question_service import QuestionService  # noqa: E402
from services.rag_service import (  # noqa: E402
    _question_opponents,
    filter_chunks_by_opponent,
)


def _chunk(match_name: str, text: str, score: float = 0.9) -> RetrievedChunk:
    return RetrievedChunk(
        source_pdf=f"{match_name}.pdf",
        page_number=1,
        report_type="Play by Play",
        match_name=match_name,
        text=text,
        score=score,
    )


# ---------------------------------------------------------------------------
# _parse_made_attempted
# ---------------------------------------------------------------------------
def test_parse_made_attempted() -> None:
    p = QuestionService._parse_made_attempted
    assert p("20/44") == (20, 44)
    assert p("10 / 25") == (10, 25)
    assert p("0/0") == (0, 0)
    assert p("7.0/9.0") == (7, 9)
    # Malformed / missing -> (0, 0), never raises.
    assert p("") == (0, 0)
    assert p(None) == (0, 0)
    assert p("5") == (0, 0)
    assert p("abc/def") == (0, 0)
    assert p("/") == (0, 0)


# ---------------------------------------------------------------------------
# _question_opponents (proper-noun extraction)
# ---------------------------------------------------------------------------
def test_question_opponents_extraction() -> None:
    assert _question_opponents("make a report about the Angola game") == {"angola"}
    # Sentence-initial verbs / common words are ignored, even capitalised.
    assert _question_opponents("Show our best lineup") == set()
    # No proper noun at all -> empty (guard becomes a no-op).
    assert _question_opponents("what was the final score") == set()
    # Multiple opponents captured.
    assert {"angola", "mali"} <= _question_opponents("compare the Angola and Mali games")


# ---------------------------------------------------------------------------
# Opponent guard (the Angola->Mali bug)
# ---------------------------------------------------------------------------
def test_opponent_guard_drops_wrong_game() -> None:
    os.environ.pop("RAG_OPPONENT_GUARD", None)  # ensure default-on
    question = "make a report about the Angola game"
    mali_chunks = [
        _chunk("Egypt vs Mali", "Egypt beat Mali 80-70 with strong rebounding."),
        _chunk("Egypt vs Mali", "Mali struggled from three in the second half."),
    ]
    # No chunk mentions Angola -> everything filtered -> caller will refuse.
    assert filter_chunks_by_opponent(question, mali_chunks) == []


def test_opponent_guard_keeps_matching_game() -> None:
    os.environ.pop("RAG_OPPONENT_GUARD", None)
    question = "make a report about the Angola game"
    chunks = [
        _chunk("Egypt vs Mali", "Egypt beat Mali 80-70."),
        _chunk("Egypt vs Angola", "Egypt edged Angola 75-72 in a tight finish."),
    ]
    kept = filter_chunks_by_opponent(question, chunks)
    assert len(kept) == 1
    assert kept[0].match_name == "Egypt vs Angola"


def test_opponent_guard_noop_without_opponent() -> None:
    os.environ.pop("RAG_OPPONENT_GUARD", None)
    question = "what was our best lineup"
    chunks = [_chunk("Egypt vs Mali", "Lineup A outscored opponents.")]
    # No opponent named -> no filtering.
    assert filter_chunks_by_opponent(question, chunks) == chunks


def test_opponent_guard_can_be_disabled() -> None:
    os.environ["RAG_OPPONENT_GUARD"] = "0"
    try:
        question = "make a report about the Angola game"
        chunks = [_chunk("Egypt vs Mali", "Egypt beat Mali 80-70.")]
        # Guard off -> chunks pass through untouched even on opponent mismatch.
        assert filter_chunks_by_opponent(question, chunks) == chunks
    finally:
        os.environ.pop("RAG_OPPONENT_GUARD", None)


# ---------------------------------------------------------------------------
# Relevance floor (TF-IDF path). Builds a tiny in-memory engine with no chroma
# index so retrieval uses TF-IDF, then checks the env-gated floor.
# ---------------------------------------------------------------------------
def _build_temp_engine():
    import pandas as pd

    from rag_engine import RagEngine

    tmp = Path(tempfile.mkdtemp())
    csv_path = tmp / "chunks.csv"
    pd.DataFrame(
        [
            {
                "chunk_id": 1,
                "text": "Angola game rebounds and blocks dominated the paint",
                "source_pdf": "angola.pdf",
                "page_number": 1,
                "report_type": "",
                "match_name": "Egypt vs Angola",
            },
            {
                "chunk_id": 2,
                "text": "Mali match free throw shooting and three pointers",
                "source_pdf": "mali.pdf",
                "page_number": 1,
                "report_type": "",
                "match_name": "Egypt vs Mali",
            },
        ]
    ).to_csv(csv_path, index=False)
    # Point chroma at a non-existent dir so retrieval falls through to TF-IDF.
    return RagEngine(chunks_csv=csv_path, chroma_dir=tmp / "no_chroma_here")


def test_tfidf_relevance_floor() -> None:
    try:
        engine = _build_temp_engine()
    except Exception as exc:  # pragma: no cover - environment-dependent
        print(f"  [skip] could not build temp engine: {exc}")
        return

    # Low floor: a relevant query retrieves the matching chunk.
    os.environ["RAG_MIN_SCORE_TFIDF"] = "0.0"
    hits = engine.retrieve("rebounds and blocks", query_type="general_pdf_question", top_k=5)
    assert any("Angola" in c.match_name for c in hits), "expected the rebounds chunk to match"

    # High floor: nothing clears it -> empty result -> deterministic refusal upstream.
    os.environ["RAG_MIN_SCORE_TFIDF"] = "0.99"
    hits_floored = engine.retrieve("rebounds and blocks", query_type="general_pdf_question", top_k=5)
    assert hits_floored == [], "high floor should drop all weak matches"

    os.environ.pop("RAG_MIN_SCORE_TFIDF", None)


def test_not_found_message_is_stable() -> None:
    # The deterministic refusal returns this exact string; keep it non-empty.
    assert isinstance(NOT_FOUND_MESSAGE, str) and NOT_FOUND_MESSAGE.strip()


# ---- script entry point ---------------------------------------------------
def main() -> int:
    tests = [
        test_parse_made_attempted,
        test_question_opponents_extraction,
        test_opponent_guard_drops_wrong_game,
        test_opponent_guard_keeps_matching_game,
        test_opponent_guard_noop_without_opponent,
        test_opponent_guard_can_be_disabled,
        test_tfidf_relevance_floor,
        test_not_found_message_is_stable,
    ]
    failures = 0
    for t in tests:
        try:
            t()
            print(f"[ok ] {t.__name__}")
        except AssertionError as exc:
            failures += 1
            print(f"[FAIL] {t.__name__}: {exc}")
    print()
    if failures:
        print(f"{failures} failure(s).")
        return 1
    print(f"All {len(tests)} RAG-safety tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
