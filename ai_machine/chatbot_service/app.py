from __future__ import annotations

import os
from pathlib import Path
from typing import Any
from uuid import uuid4

import streamlit as st

from analytics import (
    available_players_from_df,
    compare_players,
    load_box_scores,
    rank_players,
    recommend_more_minutes,
    recommend_squad,
    selected_analytics_function,
    summarize_player,
)
from extract_pdfs import BOX_SCORE_CSV, CHUNKS_CSV, DATA_PDF_DIR, build_extracted_data, list_pdf_files
from llama_client import OllamaClient, answer_from_chunks, classify_question, format_analytics_answer
from memory_store import MemoryStore, rewrite_followup_question
from rag_engine import RagEngine


st.set_page_config(
    page_title="FIBA PDF Coach Chatbot",
    page_icon="basketball",
    layout="wide",
    initial_sidebar_state="expanded",
)


CSS = """
<style>
    :root {
        color-scheme: dark;
    }
    .stApp {
        background: #0f1117;
        color: #f4f4f5;
    }
    [data-testid="stSidebar"] {
        background: #171923;
        border-right: 1px solid #2a2d3a;
    }
    .block-container {
        max-width: 980px;
        padding-top: 2rem;
        padding-bottom: 6rem;
    }
    h1, h2, h3 {
        letter-spacing: 0;
    }
    .app-title {
        font-size: 2.1rem;
        font-weight: 750;
        margin-bottom: 0.25rem;
    }
    .app-subtitle {
        color: #a1a1aa;
        margin-bottom: 1.2rem;
    }
    .card-label {
        color: #9ca3af;
        font-size: 0.78rem;
        font-weight: 700;
        text-transform: uppercase;
        margin-bottom: 0.35rem;
    }
    .status-pill {
        display: inline-block;
        border: 1px solid #374151;
        border-radius: 999px;
        padding: 0.2rem 0.65rem;
        color: #d1d5db;
        font-size: 0.82rem;
        margin-right: 0.35rem;
        margin-bottom: 0.35rem;
    }
    [data-testid="stChatMessage"] {
        border: 1px solid #2b2f3d;
        border-radius: 8px;
        padding: 1rem 1.05rem;
        margin: 0.75rem 0;
        background: #161b22;
    }
    [data-testid="stChatMessage"]:has([aria-label="Chat message from user"]) {
        background: #1d2430;
    }
    div[data-testid="stButton"] button {
        border-radius: 8px;
        border: 1px solid #343847;
        background: #1b202b;
        color: #f4f4f5;
        min-height: 2.4rem;
    }
    div[data-testid="stButton"] button:hover {
        border-color: #5b6478;
        color: #ffffff;
    }
    .stChatInput textarea {
        background: #1a1f2b;
        color: #f4f4f5;
    }
</style>
"""


EXAMPLE_QUESTIONS = [
    "Can you suggest best 3 pts shooters?",
    "Who has the highest rebounds per game?",
    "Top 5 scorers",
    "Who has the best plus/minus?",
    "Which team has the highest points?",
]


def ensure_extracted_data() -> None:
    if not BOX_SCORE_CSV.exists() or not CHUNKS_CSV.exists():
        with st.spinner("Extracting PDFs and building the local retrieval index..."):
            build_extracted_data()


@st.cache_data(show_spinner=False)
def cached_counts(box_score_mtime: float, chunks_mtime: float) -> dict[str, int]:
    df = cached_box_scores(box_score_mtime)
    chunk_count = 0
    if CHUNKS_CSV.exists():
        try:
            import pandas as pd

            chunk_count = len(pd.read_csv(CHUNKS_CSV, usecols=["chunk_id"]))
        except Exception:
            chunk_count = 0
    return {"player_rows": len(df), "chunk_rows": chunk_count}


@st.cache_resource(show_spinner=False)
def cached_rag_engine(chunks_mtime: float) -> RagEngine:
    return RagEngine()


@st.cache_data(show_spinner=False)
def cached_box_scores(box_score_mtime: float):
    return load_box_scores()


@st.cache_resource(show_spinner=False)
def cached_memory_store() -> MemoryStore:
    return MemoryStore()


def file_mtime(path: Path) -> float:
    return path.stat().st_mtime if path.exists() else 0.0


def save_uploaded_pdfs(files: list[Any]) -> int:
    DATA_PDF_DIR.mkdir(parents=True, exist_ok=True)
    saved = 0
    for uploaded in files:
        target = DATA_PDF_DIR / uploaded.name
        target.write_bytes(uploaded.getbuffer())
        saved += 1
    return saved


def render_chat_card(role: str, content: str) -> None:
    label = "Coach" if role == "user" else "Assistant"
    with st.chat_message(role):
        st.markdown(f'<div class="card-label">{label}</div>', unsafe_allow_html=True)
        st.markdown(content)


def is_development_mode() -> bool:
    return os.getenv("APP_ENV", "development").lower() in {"development", "dev", "local"}


def answer_with_rag(
    question: str,
    query_type: str = "general_pdf_question",
    client: OllamaClient | None = None,
) -> tuple[str, str]:
    client = client or OllamaClient()
    rag = cached_rag_engine(file_mtime(CHUNKS_CSV))
    chunks = rag.retrieve(question, query_type=query_type, top_k=6)
    return answer_from_chunks(question, chunks, client), rag.last_retrieval_engine


def analytics_llm_formatting_enabled() -> bool:
    return os.getenv("ENABLE_ANALYTICS_LLM_FORMATTING", "0").lower() in {"1", "true", "yes", "on"}


def answer_question(
    question: str,
    default_team: str,
    session_id: str | None = None,
    memory_store: MemoryStore | None = None,
) -> dict[str, Any]:
    client = OllamaClient()
    original_question = question
    semantic_memories: list[Any] = []
    memories_for_rewrite: list[Any] = []
    if session_id and memory_store:
        semantic_memories = memory_store.retrieve_similar(session_id, question, top_k=3)
        memories_for_rewrite = semantic_memories or memory_store.recent_messages(session_id, limit=3)
    rewritten_question, rewritten = rewrite_followup_question(question, memories_for_rewrite)

    df = cached_box_scores(file_mtime(BOX_SCORE_CSV))
    available_players = available_players_from_df(df, default_team)
    parsed = classify_question(rewritten_question, client, available_players=available_players, default_team=default_team)
    query_type = parsed.get("type", "general_pdf_question")
    route = parsed.get("route", "analytics" if query_type == "stat_query" else "rag")
    metric = parsed.get("metric")
    top_n = parsed.get("top_n")
    players = parsed.get("players") or []
    classification_source = parsed.get("_classification_source", "unknown")
    retrieval_engine = "none"
    if route == "analytics" and query_type == "player_comparison":
        analytics_function = "compare_players"
    elif route == "analytics" and query_type == "player_summary":
        analytics_function = "summarize_player"
    elif route == "analytics" and query_type in {"analytic_recommendation", "player_opportunity_recommendation"}:
        analytics_function = "recommend_more_minutes"
    elif route == "analytics" and query_type == "squad_recommendation":
        analytics_function = "recommend_squad"
    elif route == "analytics":
        analytics_function = selected_analytics_function(str(metric))
    else:
        analytics_function = f"rag_{query_type}"
    if is_development_mode():
        print("DEBUG:", flush=True)
        print(f"original_question={original_question}", flush=True)
        print(f"rewritten_question={rewritten_question}", flush=True)
        print(f"semantic_memory_used={bool(semantic_memories)}", flush=True)
        print(f"DEBUG: classification_source={classification_source}", flush=True)
        print(f"DEBUG: type={query_type} | route={route} | metric={metric} | top_n={top_n}", flush=True)
        print(
            f"DEBUG: type={query_type} | route={route} | metric={metric} | players={players} | top_n={top_n}",
            flush=True,
        )
        print(
            f"[routing] parsed_intent={query_type} selected_metric={metric} "
            f"selected_analytics_function={analytics_function}",
            flush=True,
        )

    def finish_analytics(answer: str) -> str:
        if analytics_llm_formatting_enabled():
            return format_analytics_answer(rewritten_question, answer, client)
        return answer

    if route == "analytics":
        if query_type == "player_comparison":
            answer = finish_analytics(compare_players(df, players=players, metric=str(metric), team=parsed.get("team"))["answer"])
        elif query_type == "player_summary":
            if not players:
                answer = "I couldn't identify the player from that question. Please include the player's name."
            else:
                answer = finish_analytics(summarize_player(df, player=players[0], team=parsed.get("team"))["answer"])
        elif query_type == "squad_recommendation":
            answer = finish_analytics(recommend_squad(
                    df,
                    team=parsed.get("team") or default_team,
                    top_n=int(top_n or 5),
                    strategy=str(parsed.get("strategy") or "balanced"),
                )["answer"]
            )
        elif query_type == "stat_query":
            answer = finish_analytics(rank_players(
                    df,
                    metric=str(metric),
                    team=parsed.get("team"),
                    top_n=int(top_n or 3),
                    metric_context=parsed.get("metric_context"),
                    min_attempts=int(parsed.get("min_attempts") or 0),
                )["answer"]
            )
        elif query_type in {"analytic_recommendation", "player_opportunity_recommendation"}:
            answer = finish_analytics(recommend_more_minutes(
                    df,
                    team=parsed.get("team") or default_team,
                    top_n=int(top_n or 3),
                )["answer"]
            )
        else:
            answer = "I couldn't classify that analytics question."
    else:
        answer, retrieval_engine = answer_with_rag(rewritten_question, query_type=query_type, client=client)

    if is_development_mode():
        print(f"retrieval_engine={retrieval_engine}", flush=True)

    return {
        "answer": answer,
        "parsed_intent": parsed,
        "original_question": original_question,
        "rewritten_question": rewritten_question,
        "semantic_memory_used": bool(semantic_memories),
        "rewritten": rewritten,
        "retrieval_engine": retrieval_engine,
    }


st.markdown(CSS, unsafe_allow_html=True)
ensure_extracted_data()

with st.sidebar:
    st.title("Project Info")
    st.markdown(
        """
- **Data source:** FIBA PDF reports
- **Structured answers:** Box Score CSV + Pandas
- **LLM parser:** Groq API from environment variables
- **General answers:** PDF chunks + Chroma retrieval
"""
    )

    st.divider()
    default_team = st.selectbox("Coach team focus", ["EGY", "ANG", "MLI", "UGA"], index=0)

    uploaded_files = st.file_uploader(
        "Upload FIBA PDF reports",
        type=["pdf"],
        accept_multiple_files=True,
    )
    if uploaded_files and st.button("Save uploads and rebuild"):
        saved_count = save_uploaded_pdfs(uploaded_files)
        summary = build_extracted_data()
        cached_counts.clear()
        cached_box_scores.clear()
        cached_rag_engine.clear()
        st.success(f"Saved {saved_count} PDF(s). Indexed {summary['chunk_rows']} chunks.")

    if st.button("Rebuild extraction/index"):
        summary = build_extracted_data()
        cached_counts.clear()
        cached_box_scores.clear()
        cached_rag_engine.clear()
        st.success(f"Indexed {summary['pdf_count']} PDFs.")

    st.divider()
    pdf_count = len(list_pdf_files())
    counts = cached_counts(file_mtime(BOX_SCORE_CSV), file_mtime(CHUNKS_CSV))
    st.markdown(
        f"""
<span class="status-pill">{pdf_count} PDFs</span>
<span class="status-pill">{counts['player_rows']} player rows</span>
<span class="status-pill">{counts['chunk_rows']} chunks</span>
""",
        unsafe_allow_html=True,
    )

st.markdown('<div class="app-title">FIBA PDF Coach Chatbot</div>', unsafe_allow_html=True)
st.markdown(
    '<div class="app-subtitle">Ask coaching questions. Statistical answers come from extracted box-score rows; general answers come only from retrieved PDF chunks.</div>',
    unsafe_allow_html=True,
)

st.markdown("#### Example questions")
cols = st.columns(3)
for index, example in enumerate(EXAMPLE_QUESTIONS):
    if cols[index % 3].button(example, use_container_width=True):
        st.session_state["pending_question"] = example

if "session_id" not in st.session_state:
    st.session_state["session_id"] = str(uuid4())

memory_store = cached_memory_store()
session_id = st.session_state["session_id"]

if "messages" not in st.session_state:
    stored_messages = memory_store.load_messages(session_id)
    st.session_state["messages"] = [{"role": message.role, "content": message.content} for message in stored_messages]

for message in st.session_state["messages"]:
    render_chat_card(message["role"], message["content"])

pending_question = st.session_state.pop("pending_question", None)
typed_question = st.chat_input("Ask about the uploaded FIBA reports")
question = pending_question or typed_question

if question:
    st.session_state["messages"].append({"role": "user", "content": question})
    render_chat_card("user", question)
    with st.spinner("Reading the reports and preparing the answer..."):
        result = answer_question(question, default_team=default_team, session_id=session_id, memory_store=memory_store)
        answer = result["answer"]
    parsed_intent = dict(result.get("parsed_intent") or {})
    parsed_intent["original_question"] = result.get("original_question")
    parsed_intent["rewritten_question"] = result.get("rewritten_question")
    route = parsed_intent.get("route")
    metric = parsed_intent.get("metric")
    players = parsed_intent.get("players") or []
    memory_store.save_message(session_id, "user", question, parsed_intent, route, metric, players)
    memory_store.save_message(session_id, "assistant", answer, parsed_intent, route, metric, players)
    st.session_state["messages"].append({"role": "assistant", "content": answer})
    render_chat_card("assistant", answer)
