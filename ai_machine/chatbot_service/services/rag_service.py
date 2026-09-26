from __future__ import annotations

import os
import re
from functools import lru_cache
from pathlib import Path
from typing import Iterator

from llama_client import NOT_FOUND_MESSAGE, extractive_rag_fallback
from rag_engine import RagEngine, RetrievedChunk, chunks_to_context

from .groq_client import GroqClient, GroqConfigurationError


def _env_enabled(name: str, default: str = "1") -> bool:
    return str(os.getenv(name, default)).strip().lower() not in ("0", "false", "no", "off", "")


# Words that look like proper nouns but are never opponents — sentence-initial
# verbs, question words, months, and common basketball vocabulary. Used to keep
# the opponent guard from treating an ordinary capitalised word as an opponent.
_OPP_STOPWORDS = {
    "make", "give", "show", "tell", "write", "create", "report", "summary", "summarise",
    "summarize", "analyse", "analyze", "list", "who", "what", "when", "where", "why",
    "how", "which", "the", "our", "their", "team", "game", "match", "report", "lineup",
    "lineups", "roster", "player", "players", "points", "rebounds", "assists", "steals",
    "blocks", "turnovers", "fouls", "minutes", "starter", "starters", "captain", "season",
    "quarter", "half", "first", "second", "third", "fourth", "next", "last", "previous",
    "recent", "upcoming", "coach", "coaching", "play", "defense", "offense", "i", "we",
    "january", "february", "march", "april", "may", "june", "july", "august",
    "september", "october", "november", "december", "monday", "tuesday", "wednesday",
    "thursday", "friday", "saturday", "sunday",
}


def _question_opponents(question: str) -> set[str]:
    """
    Lowercased proper-noun-looking terms from the question that plausibly name an
    opponent (e.g. 'Angola'). Returns an empty set when the question names none, in
    which case the opponent guard does not filter. Heuristic but conservative.
    """
    terms: set[str] = set()
    for match in re.finditer(r"\b([A-Z][a-zA-Z]{2,})\b", question or ""):
        word = match.group(1).lower()
        if word in _OPP_STOPWORDS:
            continue
        terms.add(word)
    return terms


def _chunk_mentions(chunk: RetrievedChunk, terms: set[str]) -> bool:
    hay = f"{chunk.match_name or ''} {chunk.text or ''}".lower()
    return any(term in hay for term in terms)


def filter_chunks_by_opponent(question: str, chunks: list[RetrievedChunk]) -> list[RetrievedChunk]:
    """
    Drop retrieved chunks that don't mention an opponent named in the question.
    If the question names a specific opponent but no chunk mentions it, returns an
    empty list so the caller refuses rather than answering about the wrong game
    (the Angola→Mali bug). No-op when no opponent is named or the guard is disabled.
    """
    if not _env_enabled("RAG_OPPONENT_GUARD", "1"):
        return chunks
    terms = _question_opponents(question)
    if not terms:
        return chunks
    return [c for c in chunks if _chunk_mentions(c, terms)]


def chunk_sources(chunks: list[RetrievedChunk]) -> list[dict[str, object]]:
    seen: set[tuple[str, int]] = set()
    sources: list[dict[str, object]] = []
    for chunk in chunks:
        key = (chunk.source_pdf, chunk.page_number)
        if key in seen:
            continue
        seen.add(key)
        sources.append(
            {
                "source_pdf": chunk.source_pdf,
                "page_number": chunk.page_number,
                "report_type": chunk.report_type,
                "match_name": chunk.match_name,
            }
        )
    return sources


def _path_mtime(path: Path) -> float:
    return path.stat().st_mtime if path.exists() else 0.0


def _dir_mtime(path: Path) -> float:
    if not path.exists():
        return 0.0
    mtimes = [_path_mtime(path)]
    try:
        mtimes.extend(item.stat().st_mtime for item in path.rglob("*") if item.is_file())
    except Exception:
        pass
    return max(mtimes)


@lru_cache(maxsize=32)
def _cached_engine(
    chunks_csv_text: str,
    chunks_mtime: float,
    chroma_dir_text: str,
    chroma_mtime: float,
) -> RagEngine:
    del chunks_mtime, chroma_mtime
    return RagEngine(chunks_csv=Path(chunks_csv_text), chroma_dir=Path(chroma_dir_text))


class RagService:
    def __init__(
        self,
        chunks_csv: Path,
        chroma_dir: Path,
        groq_client: GroqClient | None = None,
    ) -> None:
        self.engine = _cached_engine(
            str(chunks_csv),
            _path_mtime(chunks_csv),
            str(chroma_dir),
            _dir_mtime(chroma_dir),
        )
        self.groq_client = groq_client or GroqClient()

    def retrieve(self, question: str, query_type: str, top_k: int = 6) -> tuple[list[RetrievedChunk], str]:
        chunks = self.engine.retrieve(question, query_type=query_type, top_k=top_k)
        return chunks, self.engine.last_retrieval_engine

    @staticmethod
    def _build_prompt(question: str, chunks: list[RetrievedChunk]) -> tuple[str, str]:
        """The (system, prompt) pair shared by answer() and answer_stream(), so the
        streaming and non-streaming paths can never drift apart."""
        system = (
            "You are a professional basketball analyst helping a coach. "
            "Answer using ONLY the provided PDF context. "
            "If the context does not directly and specifically answer the question — "
            "including when it is about a different game, opponent, or player than asked — "
            f"reply with exactly this and nothing else: {NOT_FOUND_MESSAGE}"
        )
        prompt = f"""
Use only these retrieved PDF chunks to answer the coach's question.
Before answering, confirm the chunks actually concern the specific game, opponent,
or player the question asks about. If they do not, reply with exactly:
{NOT_FOUND_MESSAGE}

Otherwise: start with a direct answer, then add concise bullets or ranking when useful.
Include source PDF names and page numbers used.
Do not invent numbers, names, events, or conclusions, and do not substitute a
different game or opponent for the one asked about.

Question:
{question}

Retrieved PDF context:
{chunks_to_context(chunks)}
"""
        return system, prompt

    def answer(self, question: str, query_type: str) -> tuple[str, str, list[dict[str, object]]]:
        chunks, retrieval_engine = self.retrieve(question, query_type=query_type, top_k=6)
        # Deterministic refusal: nothing cleared the relevance floor.
        if not chunks:
            return NOT_FOUND_MESSAGE, retrieval_engine, []

        # Opponent guard: if the coach named an opponent and no retrieved chunk
        # mentions it, refuse instead of answering about a different game.
        chunks = filter_chunks_by_opponent(question, chunks)
        if not chunks:
            return NOT_FOUND_MESSAGE, retrieval_engine, []

        sources = chunk_sources(chunks)
        system, prompt = self._build_prompt(question, chunks)
        try:
            answer = self.groq_client.generate_text(prompt, system=system, temperature=0.1, max_tokens=800)
        except GroqConfigurationError:
            answer = extractive_rag_fallback(question, chunks)
        except Exception:
            answer = extractive_rag_fallback(question, chunks)
        return answer, retrieval_engine, sources

    def answer_stream(
        self, question: str, query_type: str, meta: dict[str, object] | None = None
    ) -> Iterator[str]:
        """Streaming variant of answer() (Phase 2a): yields the answer text in chunks.

        Deterministic refusals (no chunks / opponent guard) are yielded whole. The LLM
        answer is streamed token-by-token via ``groq_client.stream_text``; if Groq is
        unconfigured or the stream fails *before any token arrives*, the extractive
        fallback is yielded whole instead. ``retrieval_engine`` and ``sources`` are
        written into ``meta`` (when provided) so the caller can persist them after the
        stream ends — a generator can't return them alongside its yields.
        """
        meta = meta if meta is not None else {}
        chunks, retrieval_engine = self.retrieve(question, query_type=query_type, top_k=6)
        meta["retrieval_engine"] = retrieval_engine
        meta["sources"] = []
        if not chunks:
            yield NOT_FOUND_MESSAGE
            return

        chunks = filter_chunks_by_opponent(question, chunks)
        if not chunks:
            yield NOT_FOUND_MESSAGE
            return

        meta["sources"] = chunk_sources(chunks)
        system, prompt = self._build_prompt(question, chunks)
        streamed_any = False
        try:
            for piece in self.groq_client.stream_text(
                prompt, system=system, temperature=0.1, max_tokens=800
            ):
                streamed_any = True
                yield piece
        except GroqConfigurationError:
            if not streamed_any:
                yield extractive_rag_fallback(question, chunks)
        except Exception:
            if not streamed_any:
                yield extractive_rag_fallback(question, chunks)
