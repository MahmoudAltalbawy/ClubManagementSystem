from __future__ import annotations

import json
import os
import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from analytics import detect_stat_metric
from embedding_utils import embed_texts


DATA_DIR = Path("data")
EXTRACTED_DIR = Path("extracted")
CHAT_DB_PATH = DATA_DIR / "chat_history.db"
CHROMA_CHAT_MEMORY_DIR = EXTRACTED_DIR / "chroma_chat_memory"
CHAT_COLLECTION_NAME = "chat_memory"


@dataclass(frozen=True)
class MemoryMessage:
    session_id: str
    role: str
    content: str
    parsed_intent: dict[str, Any]
    route: str | None
    metric: str | None
    players: list[str]
    timestamp: str


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def json_loads_dict(value: str | None) -> dict[str, Any]:
    if not value:
        return {}
    try:
        loaded = json.loads(value)
        return loaded if isinstance(loaded, dict) else {}
    except json.JSONDecodeError:
        return {}


def json_loads_list(value: str | None) -> list[str]:
    if not value:
        return []
    try:
        loaded = json.loads(value)
        if isinstance(loaded, list):
            return [str(item) for item in loaded]
    except json.JSONDecodeError:
        pass
    return []


def metric_phrase(metric: str | None) -> str:
    labels = {
        "three_point_shooting": "3PT shooting",
        "three_made": "3PT shooting",
        "three_percentage": "3PT percentage",
        "free_throw_percentage": "free throws",
        "ft_made": "free throws made",
        "ft_attempted": "free throw attempts",
        "field_goal_percentage": "field goal percentage",
        "plus_minus": "plus/minus",
        "defensive_rebounds": "defensive rebounds",
        "offensive_rebounds": "offensive rebounds",
    }
    return labels.get(str(metric), str(metric or "").replace("_", " "))


def is_follow_up(question: str) -> bool:
    q = question.lower().strip()
    starters = (
        "what about",
        "how about",
        "and ",
        "same thing",
        "same for",
        "compare them",
        "what about them",
    )
    return q.startswith(starters) or q in {"assists?", "rebounds?", "free throws?", "steals?"}


def memory_to_dict(memory: MemoryMessage | dict[str, Any]) -> dict[str, Any]:
    if isinstance(memory, MemoryMessage):
        return {
            "session_id": memory.session_id,
            "role": memory.role,
            "content": memory.content,
            "parsed_intent": memory.parsed_intent,
            "route": memory.route,
            "metric": memory.metric,
            "players": memory.players,
            "timestamp": memory.timestamp,
        }
    return memory


def rewrite_followup_question(
    question: str,
    memories: list[MemoryMessage | dict[str, Any]],
    chronological_messages: list[MemoryMessage | dict[str, Any]] | None = None,
) -> tuple[str, bool]:
    if not is_follow_up(question):
        return question, False

    metric = detect_stat_metric(question)
    if not metric:
        return question, False

    candidates = list(memories)
    if chronological_messages:
        candidates.extend(chronological_messages)

    for memory in candidates:
        item = memory_to_dict(memory)
        intent = item.get("parsed_intent") or {}
        players = intent.get("players") or item.get("players") or []
        if intent.get("type") == "player_comparison" and len(players) >= 2:
            return f"Compare {players[0]} and {players[1]} in {metric_phrase(metric)}", True

    for memory in candidates:
        item = memory_to_dict(memory)
        intent = item.get("parsed_intent") or {}
        if intent.get("type") == "stat_query":
            top_n = int(intent.get("top_n") or 3)
            team = intent.get("team") or item.get("team") or "EGY"
            return f"Top {top_n} {team} players for {metric_phrase(metric)}", True

    return question, False


class MemoryStore:
    """
    Chat history + (optional) Chroma semantic memory.

    Backend selection: if the CHAT_HISTORY_DSN env var is set, the relational store
    is Postgres (one shared DB, rows scoped by project_id) so the .NET app and other
    services can read transcripts; otherwise it stays per-project SQLite (the default,
    unchanged behaviour). Chroma is untouched either way. The SQL is kept simple and
    placeholder-parameterised so both backends share the same code paths.
    """

    def __init__(
        self,
        db_path: Path = CHAT_DB_PATH,
        chroma_dir: Path = CHROMA_CHAT_MEMORY_DIR,
        enable_chroma: bool = True,
        project_id: str | None = None,
    ) -> None:
        self.db_path = db_path
        self.chroma_dir = chroma_dir
        self.project_id = project_id or "default"
        self.collection = None
        self._dsn = os.getenv("CHAT_HISTORY_DSN", "").strip()
        self.backend = "postgres" if self._dsn else "sqlite"
        self._ph = "%s" if self.backend == "postgres" else "?"
        if self.backend == "sqlite":
            self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._init_db()
        if enable_chroma:
            self._init_chroma()

    def _connect(self) -> Any:
        if self.backend == "postgres":
            import psycopg  # psycopg 3; only imported when a DSN is configured

            return psycopg.connect(self._dsn)
        conn = sqlite3.connect(self.db_path)
        # Phase 2.5d: WAL lets readers (transcript / memory retrieval) proceed without
        # blocking the writer (save_message) and vice-versa, so concurrent /ask turns
        # don't serialize on the chat DB. Best-effort: a filesystem that can't do WAL
        # (some network mounts) silently keeps the default journal mode.
        try:
            conn.execute("PRAGMA journal_mode=WAL")
        except sqlite3.Error:
            pass
        return conn

    # Postgres scopes rows by project_id (shared DB); SQLite already isolates by file,
    # and legacy rows predate the column, so it is NOT filtered there.
    def _scope_sql(self) -> str:
        return f" AND project_id = {self._ph}" if self.backend == "postgres" else ""

    def _scope_params(self) -> tuple:
        return (self.project_id,) if self.backend == "postgres" else ()

    def _init_db(self) -> None:
        conn = self._connect()
        try:
            if self.backend == "postgres":
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS messages (
                        id BIGSERIAL PRIMARY KEY,
                        project_id TEXT NOT NULL DEFAULT 'default',
                        session_id TEXT NOT NULL,
                        role TEXT NOT NULL,
                        content TEXT NOT NULL,
                        parsed_intent TEXT NOT NULL DEFAULT '{}',
                        route TEXT,
                        metric TEXT,
                        players TEXT NOT NULL DEFAULT '[]',
                        timestamp TEXT NOT NULL
                    )
                    """
                )
                conn.execute(
                    "CREATE INDEX IF NOT EXISTS idx_messages_project_session_time "
                    "ON messages(project_id, session_id, timestamp)"
                )
            else:
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS messages (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        project_id TEXT NOT NULL DEFAULT 'default',
                        session_id TEXT NOT NULL,
                        role TEXT NOT NULL,
                        content TEXT NOT NULL,
                        parsed_intent TEXT NOT NULL DEFAULT '{}',
                        route TEXT,
                        metric TEXT,
                        players TEXT NOT NULL DEFAULT '[]',
                        timestamp TEXT NOT NULL
                    )
                    """
                )
                # Legacy DBs created before project_id existed: add it in place.
                cols = {row[1] for row in conn.execute("PRAGMA table_info(messages)").fetchall()}
                if "project_id" not in cols:
                    conn.execute("ALTER TABLE messages ADD COLUMN project_id TEXT NOT NULL DEFAULT 'default'")
                conn.execute("CREATE INDEX IF NOT EXISTS idx_messages_session_time ON messages(session_id, timestamp)")
            conn.commit()
        finally:
            conn.close()

    def _init_chroma(self) -> None:
        try:
            import chromadb

            self.chroma_dir.mkdir(parents=True, exist_ok=True)
            client = chromadb.PersistentClient(path=str(self.chroma_dir))
            self.collection = client.get_or_create_collection(CHAT_COLLECTION_NAME)
        except Exception:
            self.collection = None

    def save_message(
        self,
        session_id: str,
        role: str,
        content: str,
        parsed_intent: dict[str, Any] | None = None,
        route: str | None = None,
        metric: str | None = None,
        players: list[str] | None = None,
        timestamp: str | None = None,
    ) -> int:
        timestamp = timestamp or utc_now_iso()
        parsed_intent = parsed_intent or {}
        players = players or []
        params = (
            self.project_id,
            session_id,
            role,
            content,
            json.dumps(parsed_intent),
            route,
            metric,
            json.dumps(players),
            timestamp,
        )
        placeholders = ", ".join([self._ph] * len(params))
        conn = self._connect()
        try:
            insert_sql = (
                "INSERT INTO messages"
                "(project_id, session_id, role, content, parsed_intent, route, metric, players, timestamp) "
                f"VALUES ({placeholders})"
            )
            if self.backend == "postgres":
                cursor = conn.execute(insert_sql + " RETURNING id", params)
                message_id = int(cursor.fetchone()[0])
            else:
                cursor = conn.execute(insert_sql, params)
                message_id = int(cursor.lastrowid)
            conn.commit()
        finally:
            conn.close()
        self._save_vector(message_id, session_id, role, content, parsed_intent, route, metric, players, timestamp)
        return message_id

    def _save_vector(
        self,
        message_id: int,
        session_id: str,
        role: str,
        content: str,
        parsed_intent: dict[str, Any],
        route: str | None,
        metric: str | None,
        players: list[str],
        timestamp: str,
    ) -> None:
        if self.collection is None:
            return
        try:
            embedding = embed_texts([content])[0]
            self.collection.add(
                ids=[f"message-{message_id}"],
                documents=[content],
                embeddings=[embedding],
                metadatas=[
                    {
                        "session_id": session_id,
                        "role": role,
                        "content": content,
                        "timestamp": timestamp,
                        "intent_type": str(parsed_intent.get("type") or ""),
                        "parsed_intent": json.dumps(parsed_intent),
                        "route": str(route or ""),
                        "metric": str(metric or ""),
                        "players": json.dumps(players),
                    }
                ],
            )
        except Exception:
            return

    def load_messages(self, session_id: str) -> list[MemoryMessage]:
        conn = self._connect()
        try:
            rows = conn.execute(
                f"""
                SELECT session_id, role, content, parsed_intent, route, metric, players, timestamp
                FROM messages
                WHERE session_id = {self._ph}{self._scope_sql()}
                ORDER BY timestamp ASC, id ASC
                """,
                (session_id, *self._scope_params()),
            ).fetchall()
        finally:
            conn.close()
        return [
            MemoryMessage(
                session_id=str(row[0]),
                role=str(row[1]),
                content=str(row[2]),
                parsed_intent=json_loads_dict(row[3]),
                route=row[4],
                metric=row[5],
                players=json_loads_list(row[6]),
                timestamp=str(row[7]),
            )
            for row in rows
        ]

    def recent_messages(self, session_id: str, limit: int = 3) -> list[MemoryMessage]:
        conn = self._connect()
        try:
            rows = conn.execute(
                f"""
                SELECT session_id, role, content, parsed_intent, route, metric, players, timestamp
                FROM messages
                WHERE session_id = {self._ph}{self._scope_sql()}
                ORDER BY timestamp DESC, id DESC
                LIMIT {self._ph}
                """,
                (session_id, *self._scope_params(), limit),
            ).fetchall()
        finally:
            conn.close()
        messages = [
            MemoryMessage(
                session_id=str(row[0]),
                role=str(row[1]),
                content=str(row[2]),
                parsed_intent=json_loads_dict(row[3]),
                route=row[4],
                metric=row[5],
                players=json_loads_list(row[6]),
                timestamp=str(row[7]),
            )
            for row in rows
        ]
        return list(reversed(messages))

    def retrieve_similar(self, session_id: str, question: str, top_k: int = 3) -> list[dict[str, Any]]:
        if self.collection is None:
            return []
        try:
            query_embedding = embed_texts([question])[0]
            results = self.collection.query(
                query_embeddings=[query_embedding],
                n_results=top_k,
                where={"session_id": session_id},
                include=["documents", "metadatas", "distances"],
            )
            documents = results.get("documents", [[]])[0]
            metadatas = results.get("metadatas", [[]])[0]
            distances = results.get("distances", [[]])[0]
            memories: list[dict[str, Any]] = []
            for document, metadata, distance in zip(documents, metadatas, distances):
                players = json_loads_list(str(metadata.get("players", "")))
                if not players:
                    players = [player.strip() for player in str(metadata.get("players", "")).split(",") if player.strip()]
                parsed_intent = json_loads_dict(str(metadata.get("parsed_intent", "")))
                if not parsed_intent:
                    parsed_intent = {"type": str(metadata.get("intent_type", "")), "players": players}
                memories.append(
                    {
                        "session_id": str(metadata.get("session_id", session_id)),
                        "role": str(metadata.get("role", "")),
                        "content": str(metadata.get("content") or document or ""),
                        "parsed_intent": parsed_intent,
                        "route": str(metadata.get("route", "")),
                        "metric": str(metadata.get("metric", "")),
                        "players": players,
                        "timestamp": str(metadata.get("timestamp", "")),
                        "score": 1.0 / (1.0 + float(distance or 0.0)),
                    }
                )
            return memories
        except Exception:
            return []

    def list_sessions(self, limit: int = 100) -> list[dict[str, Any]]:
        """
        Session index for this project: one row per session_id with message count,
        first/last timestamps, and a short title taken from the first user message.
        Ordered most-recent-activity first.
        """
        conn = self._connect()
        try:
            rows = conn.execute(
                f"""
                SELECT session_id,
                       COUNT(*) AS message_count,
                       MIN(timestamp) AS started_at,
                       MAX(timestamp) AS last_at
                FROM messages
                WHERE 1 = 1{self._scope_sql()}
                GROUP BY session_id
                ORDER BY MAX(timestamp) DESC
                LIMIT {self._ph}
                """,
                (*self._scope_params(), limit),
            ).fetchall()
            summaries: list[dict[str, Any]] = []
            for row in rows:
                session_id = str(row[0])
                title_row = conn.execute(
                    f"""
                    SELECT content FROM messages
                    WHERE session_id = {self._ph}{self._scope_sql()} AND role = {self._ph}
                    ORDER BY timestamp ASC, id ASC
                    LIMIT 1
                    """,
                    (session_id, *self._scope_params(), "user"),
                ).fetchone()
                title = str(title_row[0]) if title_row else ""
                if len(title) > 80:
                    title = title[:77] + "..."
                summaries.append(
                    {
                        "session_id": session_id,
                        "message_count": int(row[1]),
                        "started_at": str(row[2] or ""),
                        "last_at": str(row[3] or ""),
                        "title": title,
                    }
                )
        finally:
            conn.close()
        return summaries

    def clear_session(self, session_id: str) -> None:
        conn = self._connect()
        try:
            conn.execute(
                f"DELETE FROM messages WHERE session_id = {self._ph}{self._scope_sql()}",
                (session_id, *self._scope_params()),
            )
            conn.commit()
        finally:
            conn.close()

        if self.collection is None:
            return
        try:
            self.collection.delete(where={"session_id": session_id})
        except Exception:
            return
