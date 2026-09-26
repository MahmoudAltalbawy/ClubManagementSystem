"""
Pull-based ingestion of match-stats PDFs the .NET app announces via webhook.

The app never pushes file bytes; it sends signed pull-URLs that point back at its
own internal, service-token-guarded endpoints. This service:

  1. pulls each announced PDF into the per-team project's pdf folder,
  2. records the app's canonical box-score text (trusted verbatim),
  3. rebuilds the per-team RAG index (box-score CSV + chunks + Chroma).

IMPORTANT — filenames: the extractor classifies a PDF by phrases in its filename
(detect_report_type: "box score", "plusminus", "line up", "play by play"). The app
sends machine pdf_type tokens (box_score / plus_minus / lineup / play_by_play), so
we MUST rename pulled files to the canonical report-type phrase or they'd be parsed
as "Unknown" and silently dropped from both the model and the index.

This whole method runs inside the per-team serialized worker (TeamTaskQueue), so an
ingest and a retrain for the same team never overlap. Model retrain is wired on top
of this in Phase 4.
"""
from __future__ import annotations

import os
import re
from pathlib import Path

import requests

from services.extraction_service import ExtractionService
from services.project_store import ProjectStore

# pdf_type token (from the app) → canonical filename phrase the extractor recognises.
_REPORT_TYPE_PHRASE = {
    "box_score": "FIBA Box Score",
    "plus_minus": "Player PlusMinus Summary",
    "lineup": "Line Up Analysis",
    "play_by_play": "Play by Play",
}
_SAFE_SLUG_RE = re.compile(r"[^A-Za-z0-9 _.-]+")


class WebhookIngestService:
    def __init__(
        self,
        store: ProjectStore,
        extraction: ExtractionService | None = None,
        timeout: int | None = None,
    ) -> None:
        self.store = store
        self.extraction = extraction or ExtractionService(store)
        self.timeout = timeout or int(os.getenv("WEBHOOK_PULL_TIMEOUT", "30"))

    def _pull_headers(self) -> dict[str, str]:
        token = os.getenv("MICROSERVICE_SERVICE_TOKEN", "")
        # The app's internal pull endpoint requires the same shared service token.
        return {"Authorization": f"Bearer {token}"} if token else {}

    @classmethod
    def _stored_filename(cls, pdf_type: str, match_stats_id: str) -> str:
        token = (pdf_type or "").strip().lower()
        phrase = _REPORT_TYPE_PHRASE.get(token)
        safe_id = _SAFE_SLUG_RE.sub("", match_stats_id) or "match"
        if phrase is None:
            # Unknown type: keep a deterministic, filesystem-safe name. It won't be
            # classified by the model extractor, but it's still indexed for RAG text.
            safe_token = _SAFE_SLUG_RE.sub("", token) or "document"
            return f"{safe_token} {safe_id}.pdf"
        # Deterministic name keyed by match so re-uploads of the same type overwrite.
        return f"{phrase} {safe_id}.pdf"

    def pull(self, payload) -> dict:
        """Pull announced PDFs + canonical box-score text. Returns what was saved."""
        project_id = self.store.create_project(payload.team_id)  # project_id == team_id

        pdf_dir = self.store.pdf_dir(project_id)
        pdf_dir.mkdir(parents=True, exist_ok=True)
        extracted_dir = self.store.extracted_dir(project_id)
        extracted_dir.mkdir(parents=True, exist_ok=True)

        # Persist the app's canonical box-score text so downstream consumers can
        # trust it verbatim instead of re-parsing the box-score PDF.
        if payload.box_score_text:
            box_path = extracted_dir / f"box_score_app_{payload.match_stats_id}.txt"
            # Phase 2.5d: write atomically (temp + os.replace) so a concurrent reader
            # of the canonical box-score text never sees a half-written file.
            tmp_path = box_path.with_name(f"{box_path.name}.tmp.{os.getpid()}")
            tmp_path.write_text(payload.box_score_text, encoding="utf-8")
            os.replace(tmp_path, box_path)

        headers = self._pull_headers()
        pulled: list[str] = []
        for doc in payload.documents:
            filename = self._stored_filename(doc.pdf_type, payload.match_stats_id)
            target = pdf_dir / filename
            resp = requests.get(doc.pull_url, headers=headers, timeout=self.timeout)
            resp.raise_for_status()
            target.write_bytes(resp.content)
            pulled.append(filename)

        return {
            "project_id": project_id,
            "match_stats_id": payload.match_stats_id,
            "pulled_files": pulled,
        }

    def ingest(self, payload) -> dict:
        """
        Full ingestion job (runs on the per-team worker): pull, then rebuild the RAG
        index for the team's project. Box-score CSV + chunks + Chroma are refreshed
        so "Ask Equipo" answers reflect the new match immediately.
        """
        result = self.pull(payload)
        rebuild = self.extraction.rebuild(result["project_id"])
        result["rebuild"] = rebuild
        return result
