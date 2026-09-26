from __future__ import annotations

from pathlib import Path
from shutil import copy2

from extract_pdfs import build_extracted_data

from .project_store import ProjectStore


class ExtractionService:
    def __init__(self, store: ProjectStore | None = None) -> None:
        self.store = store or ProjectStore()

    def copy_pdf(self, project_id: str, source_pdf: Path) -> str:
        self.store.ensure_project(project_id)
        if source_pdf.suffix.lower() != ".pdf":
            raise ValueError(f"{source_pdf.name} is not a PDF")
        target = self.store.pdf_dir(project_id) / source_pdf.name
        copy2(source_pdf, target)
        return target.name

    def rebuild(self, project_id: str) -> dict[str, object]:
        self.store.ensure_project(project_id)
        summary = build_extracted_data(
            pdf_dir=self.store.pdf_dir(project_id),
            extracted_dir=self.store.extracted_dir(project_id),
            include_legacy=False,
        )
        vector_index = "chroma" if summary.get("chroma_index_built") else "tfidf_fallback"
        return {
            "project_id": project_id,
            "status": "ready",
            "pdf_count": summary["pdf_count"],
            "player_rows": summary["player_rows"],
            "chunk_rows": summary["chunk_rows"],
            "vector_index": vector_index,
        }

    def rebuild_session(self, project_id: str, session_id: str) -> dict[str, object]:
        """
        Build an isolated index over ONLY the PDFs uploaded to one chat session
        (scope=session), so a doc-only question is answered strictly from that
        file and never merged into the team corpus.
        """
        self.store.ensure_session(project_id, session_id)
        summary = build_extracted_data(
            pdf_dir=self.store.session_pdf_dir(project_id, session_id),
            extracted_dir=self.store.session_extracted_dir(project_id, session_id),
            include_legacy=False,
        )
        vector_index = "chroma" if summary.get("chroma_index_built") else "tfidf_fallback"
        return {
            "project_id": project_id,
            "session_id": session_id,
            "status": "ready",
            "pdf_count": summary["pdf_count"],
            "player_rows": summary["player_rows"],
            "chunk_rows": summary["chunk_rows"],
            "vector_index": vector_index,
        }

