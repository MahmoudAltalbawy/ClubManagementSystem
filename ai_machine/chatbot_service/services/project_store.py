from __future__ import annotations

import re
from pathlib import Path
from uuid import uuid4


PROJECT_ID_RE = re.compile(r"^[A-Za-z0-9_.-]+$")


class ProjectStore:
    def __init__(self, root: Path | str = Path("data/projects")) -> None:
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)

    def validate_project_id(self, project_id: str) -> str:
        clean = str(project_id or "").strip()
        if not clean:
            raise ValueError("project_id cannot be empty")
        if not PROJECT_ID_RE.fullmatch(clean):
            raise ValueError("project_id may contain only letters, numbers, dots, underscores, and hyphens")
        return clean

    def create_project(self, project_id: str | None = None) -> str:
        clean = self.validate_project_id(project_id or str(uuid4()))
        self.pdf_dir(clean).mkdir(parents=True, exist_ok=True)
        self.extracted_dir(clean).mkdir(parents=True, exist_ok=True)
        return clean

    def project_dir(self, project_id: str) -> Path:
        clean = self.validate_project_id(project_id)
        return self.root / clean

    def pdf_dir(self, project_id: str) -> Path:
        return self.project_dir(project_id) / "pdfs"

    def extracted_dir(self, project_id: str) -> Path:
        return self.project_dir(project_id) / "extracted"

    def box_score_csv(self, project_id: str) -> Path:
        return self.extracted_dir(project_id) / "players_box_scores.csv"

    def chunks_csv(self, project_id: str) -> Path:
        return self.extracted_dir(project_id) / "pdf_chunks.csv"

    def chroma_pdf_index_dir(self, project_id: str) -> Path:
        return self.extracted_dir(project_id) / "chroma_pdf_index"

    def chroma_chat_memory_dir(self, project_id: str) -> Path:
        return self.extracted_dir(project_id) / "chroma_chat_memory"

    def chat_db_path(self, project_id: str) -> Path:
        return self.project_dir(project_id) / "chat_history.db"

    # ------------------------------------------------------------------
    # Session-scoped PDF index ("ask this PDF only"). A PDF uploaded with
    # scope=session is isolated under sessions/{session_id}/ and indexed on
    # its own, so a doc-only question is answered strictly from that file and
    # never merged into the team corpus.
    # ------------------------------------------------------------------
    def validate_session_id(self, session_id: str) -> str:
        clean = str(session_id or "").strip()
        if not clean:
            raise ValueError("session_id cannot be empty")
        if not PROJECT_ID_RE.fullmatch(clean):
            raise ValueError("session_id may contain only letters, numbers, dots, underscores, and hyphens")
        return clean

    def session_dir(self, project_id: str, session_id: str) -> Path:
        clean_session = self.validate_session_id(session_id)
        return self.project_dir(project_id) / "sessions" / clean_session

    def session_pdf_dir(self, project_id: str, session_id: str) -> Path:
        return self.session_dir(project_id, session_id) / "pdfs"

    def session_extracted_dir(self, project_id: str, session_id: str) -> Path:
        return self.session_dir(project_id, session_id) / "extracted"

    def session_chunks_csv(self, project_id: str, session_id: str) -> Path:
        return self.session_extracted_dir(project_id, session_id) / "pdf_chunks.csv"

    def session_chroma_dir(self, project_id: str, session_id: str) -> Path:
        return self.session_extracted_dir(project_id, session_id) / "chroma_pdf_index"

    def ensure_session(self, project_id: str, session_id: str) -> None:
        self.ensure_project(project_id)
        self.session_pdf_dir(project_id, session_id).mkdir(parents=True, exist_ok=True)
        self.session_extracted_dir(project_id, session_id).mkdir(parents=True, exist_ok=True)

    def session_has_index(self, project_id: str, session_id: str) -> bool:
        try:
            return self.session_chunks_csv(project_id, session_id).exists()
        except ValueError:
            return False

    def ensure_project(self, project_id: str) -> None:
        self.create_project(project_id)

    def pdf_count(self, project_id: str) -> int:
        folder = self.pdf_dir(project_id)
        return len(list(folder.glob("*.pdf"))) if folder.exists() else 0

    def predictions_csv(self, project_id: str) -> Path:
        return self.project_dir(project_id) / "model" / "test_predictions.csv"

    def status(self, project_id: str) -> dict[str, object]:
        self.ensure_project(project_id)
        has_box = self.box_score_csv(project_id).exists()
        has_chunks = self.chunks_csv(project_id).exists()
        has_chroma = self.chroma_pdf_index_dir(project_id).exists()
        has_predictions = self.predictions_csv(project_id).exists()
        return {
            "project_id": project_id,
            "pdf_count": self.pdf_count(project_id),
            "has_box_score_csv": has_box,
            "has_chunks_csv": has_chunks,
            "has_chroma_index": has_chroma,
            "has_predictions_csv": has_predictions,
            "status": "ready" if has_box and has_chunks else "needs_rebuild",
        }

