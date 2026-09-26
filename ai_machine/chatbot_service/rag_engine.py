from __future__ import annotations

import hashlib
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


def _env_float(name: str, default: float) -> float:
    """Read a float env var, returning the default on missing/blank/invalid."""
    raw = os.getenv(name)
    if raw is None or not str(raw).strip():
        return default
    try:
        return float(raw)
    except (TypeError, ValueError):
        return default


# Relevance floors. A retrieved chunk is dropped when its similarity score falls
# below the floor for its engine, so an off-topic top-k hit no longer becomes an
# answer. Chroma score = 1/(1+distance) ∈ (0,1]; TF-IDF score is cosine ∈ [0,1].
# Both tunable at deploy time; defaults stay conservative so genuine matches pass.
RAG_MIN_SCORE_CHROMA = _env_float("RAG_MIN_SCORE_CHROMA", 0.0)
RAG_MIN_SCORE_TFIDF = _env_float("RAG_MIN_SCORE_TFIDF", 0.05)

import pandas as pd
from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.metrics.pairwise import linear_kernel

from embedding_utils import embed_texts
from extract_pdfs import CHUNKS_CSV, EXTRACTED_DIR


CHROMA_PDF_INDEX_DIR = EXTRACTED_DIR / "chroma_pdf_index"
PDF_COLLECTION_NAME = "pdf_chunks"

REPORT_TYPE_BY_QUERY = {
    "play_by_play_question": "Play by Play",
    "lineup_question": "Line Up Analysis",
    "plus_minus_question": "Player PlusMinus Summary",
}


@dataclass(frozen=True)
class RetrievedChunk:
    source_pdf: str
    page_number: int
    report_type: str
    match_name: str
    text: str
    score: float

    def context_block(self) -> str:
        return (
            f"Source: {self.source_pdf} | Page: {self.page_number} | "
            f"Report: {self.report_type} | Match: {self.match_name}\n{self.text}"
        )


def chroma_available() -> bool:
    try:
        import chromadb  # noqa: F401
    except Exception:
        return False
    return True


def metadata_from_row(row: pd.Series) -> dict[str, str | int]:
    return {
        "source_pdf": str(row.get("source_pdf", "")),
        "page_number": int(row.get("page_number", 0) or 0),
        "report_type": str(row.get("report_type", "")),
        "match_name": str(row.get("match_name", "")),
        "chunk_id": int(row.get("chunk_id", 0) or 0),
        "text": str(row.get("text", "")),
    }


def _chunk_hash(text: str) -> str:
    """Content id for a chunk: SHA-1 of its stripped text. Identical text → identical
    id, so an unchanged chunk keeps the same id and is never re-embedded across
    rebuilds (Phase 2.5e)."""
    return hashlib.sha1(text.strip().encode("utf-8")).hexdigest()


def build_chroma_pdf_index(
    chunks_csv: Path = CHUNKS_CSV,
    persist_dir: Path = CHROMA_PDF_INDEX_DIR,
) -> bool:
    """(Re)build the PDF Chroma index incrementally (Phase 2.5e / 2.5d).

    Chunks are content-addressed — the Chroma id is a hash of the chunk text — so a
    rebuild embeds ONLY chunks whose text is new or changed, and deletes ids whose
    content disappeared. Re-ingesting an unchanged corpus performs zero embeddings;
    adding one PDF embeds only that PDF's chunks. Because it upserts/deletes on the
    live collection instead of rmtree-ing and rebuilding from scratch, there is no
    window where a concurrent reader sees a missing/half-built index. Fail-soft:
    any error returns False and leaves the existing index untouched.
    """
    if not chunks_csv.exists():
        return False
    df = pd.read_csv(chunks_csv).fillna("")

    try:
        import chromadb

        # Desired state: one entry per unique chunk text, keyed by content hash.
        desired: dict[str, tuple[str, dict]] = {}
        for _, row in df.iterrows():
            text = str(row.get("text", ""))
            if not text.strip():
                continue
            cid = f"chunk-{_chunk_hash(text)}"
            if cid not in desired:
                desired[cid] = (text, metadata_from_row(row))

        persist_dir.mkdir(parents=True, exist_ok=True)
        client = chromadb.PersistentClient(path=str(persist_dir))
        collection = client.get_or_create_collection(PDF_COLLECTION_NAME)

        # Ids currently in the index. include=[] asks for ids only (fallback for older
        # chromadb that doesn't accept an empty include list).
        try:
            existing_ids = set(collection.get(include=[]).get("ids", []) or [])
        except Exception:
            existing_ids = set(collection.get().get("ids", []) or [])

        desired_ids = set(desired.keys())
        to_delete = [cid for cid in existing_ids if cid not in desired_ids]
        to_add = [cid for cid in desired_ids if cid not in existing_ids]

        # Drop chunks whose source content is gone (e.g. a removed PDF).
        if to_delete:
            collection.delete(ids=to_delete)
        # Embed + add only the genuinely new/changed chunks — the whole point of 2.5e.
        if to_add:
            add_texts = [desired[cid][0] for cid in to_add]
            add_metas = [desired[cid][1] for cid in to_add]
            add_embeddings = embed_texts(add_texts)
            collection.add(
                ids=to_add,
                documents=add_texts,
                embeddings=add_embeddings,
                metadatas=add_metas,
            )
        # True when the index holds content; False for an emptied corpus (after the
        # delete above clears any stale vectors), matching the old empty-df contract.
        return bool(desired_ids)
    except Exception:
        return False


class RagEngine:
    def __init__(
        self,
        chunks_csv: Path = CHUNKS_CSV,
        chroma_dir: Path = CHROMA_PDF_INDEX_DIR,
    ) -> None:
        self.chunks_csv = chunks_csv
        self.chroma_dir = chroma_dir
        self.df = pd.DataFrame()
        self.vectorizer: TfidfVectorizer | None = None
        self.matrix = None
        self.chroma_collection = None
        self.last_retrieval_engine = "none"
        self.load()

    def load(self) -> None:
        if not self.chunks_csv.exists():
            return
        df = pd.read_csv(self.chunks_csv).fillna("")
        if df.empty:
            return
        self.df = df
        self._load_chroma()
        self.vectorizer = TfidfVectorizer(
            stop_words="english",
            ngram_range=(1, 2),
            max_features=20000,
        )
        self.matrix = self.vectorizer.fit_transform(self.df["text"].astype(str).tolist())

    def _load_chroma(self) -> None:
        if not self.chroma_dir.exists():
            return
        try:
            import chromadb

            client = chromadb.PersistentClient(path=str(self.chroma_dir))
            self.chroma_collection = client.get_collection(PDF_COLLECTION_NAME)
        except Exception:
            self.chroma_collection = None

    def retrieve(
        self,
        question: str,
        query_type: str = "general_pdf_question",
        top_k: int = 5,
    ) -> list[RetrievedChunk]:
        chunks = self._retrieve_chroma(question, query_type=query_type, top_k=top_k)
        if chunks:
            self.last_retrieval_engine = "chroma"
            return chunks

        chunks = self._retrieve_tfidf(question, query_type=query_type, top_k=top_k)
        self.last_retrieval_engine = "tfidf" if chunks else "none"
        return chunks

    def _retrieve_chroma(
        self,
        question: str,
        query_type: str,
        top_k: int,
    ) -> list[RetrievedChunk]:
        if self.chroma_collection is None:
            return []
        try:
            query_embedding = embed_texts([question])[0]
            where = None
            report_type = REPORT_TYPE_BY_QUERY.get(query_type)
            if report_type:
                where = {"report_type": report_type}
            results = self.chroma_collection.query(
                query_embeddings=[query_embedding],
                n_results=top_k,
                where=where,
                include=["documents", "metadatas", "distances"],
            )
            documents = results.get("documents", [[]])[0]
            metadatas = results.get("metadatas", [[]])[0]
            distances = results.get("distances", [[]])[0]
            floor = _env_float("RAG_MIN_SCORE_CHROMA", RAG_MIN_SCORE_CHROMA)
            chunks: list[RetrievedChunk] = []
            for document, metadata, distance in zip(documents, metadatas, distances):
                score = 1.0 / (1.0 + float(distance or 0.0))
                if score < floor:
                    continue
                chunks.append(
                    RetrievedChunk(
                        source_pdf=str(metadata.get("source_pdf", "")),
                        page_number=int(metadata.get("page_number", 0) or 0),
                        report_type=str(metadata.get("report_type", "")),
                        match_name=str(metadata.get("match_name", "")),
                        text=str(metadata.get("text") or document or ""),
                        score=score,
                    )
                )
            return chunks
        except Exception:
            return []

    def _retrieve_tfidf(
        self,
        question: str,
        query_type: str,
        top_k: int,
    ) -> list[RetrievedChunk]:
        if self.df.empty or self.vectorizer is None or self.matrix is None:
            return []

        query_vector = self.vectorizer.transform([question])
        scores = linear_kernel(query_vector, self.matrix).ravel()
        candidates = self.df.copy()
        candidates["_score"] = scores

        report_type = REPORT_TYPE_BY_QUERY.get(query_type)
        if report_type:
            typed = candidates[candidates["report_type"] == report_type]
            if not typed.empty:
                candidates = typed

        floor = max(_env_float("RAG_MIN_SCORE_TFIDF", RAG_MIN_SCORE_TFIDF), 0.0)
        # Strictly above the floor (and always above pure-zero token overlap).
        candidates = candidates[candidates["_score"] > floor].sort_values("_score", ascending=False).head(top_k)
        return [
            RetrievedChunk(
                source_pdf=str(row["source_pdf"]),
                page_number=int(row["page_number"]),
                report_type=str(row["report_type"]),
                match_name=str(row["match_name"]),
                text=str(row["text"]),
                score=float(row["_score"]),
            )
            for _, row in candidates.iterrows()
        ]


def chunks_to_context(chunks: Iterable[RetrievedChunk]) -> str:
    return "\n\n---\n\n".join(chunk.context_block() for chunk in chunks)
