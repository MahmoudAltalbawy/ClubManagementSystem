# Equipo Chatbot — Phased Engineering Plan

Scope of this document: everything still open after the Phase 0 data-unification
fixes that are already merged. It covers four threads the team asked to plan in
detail:

1. **Data correctness** — eliminate the PDF-vs-DB double-counting by pushing the
   union/dedup **down into the .NET/PostgreSQL layer** (a unified stats view) and
   making the chatbot a thin consumer, not a pandas shadow-ETL.
2. **Latency** — why a request is slow today, why it gets worse on a remote
   deployment, and the concrete fixes (streaming, fewer LLM round trips, pooling,
   parallelism, caching, warm singletons).
3. **Concurrency & deployment hardening** — the seams that only bite once we add
   async/streaming or run more than one worker (async event-loop blocking, cache
   incoherence across workers, startup readiness, file-write races, request-path
   memory).
4. **Postponed feature phases** — the local NLP intent classifier + NER, the
   Gemini/Grok vision + web-search integration, and the scale-out (Celery/Redis)
   work.

Each phase lists its goal, the rationale, the exact files to add or change, the
implementation steps, new environment variables, tests, risks, and acceptance
criteria. Phases are ordered by recommended execution sequence; later feature
phases (3–5) are independent of each other and can be reordered.

This revision folds in two rounds of architectural review. The headline change:
**the union/dedup is no longer a pandas job in Python** — it belongs in the database
that owns both planes. See Phase 1.

---

## Reference: current architecture and file map

The microservice lives in `SportsPlatform.Auth.Api/scripts/final chatbot/`.

| File | Role |
|---|---|
| `api/main.py` | FastAPI app. `/projects/{id}/ask` is a **synchronous `def`** (`ask_project`) — blocks, no streaming. Also `/pdfs`, `/rebuild`, `/status`, `/sessions`. |
| `services/question_service.py` | `QuestionService.ask()` — the orchestrator: memory retrieval → follow-up rewrite → box-score load → classify → hybrid lanes → analytics/RAG → save memory. Holds all `_looks_like_*` detectors and lane handlers. |
| `services/rag_service.py` | `RagService.answer()` — RAG retrieval + Groq generation, opponent guard, deterministic refusal. |
| `rag_engine.py` | `RagEngine` — Chroma (vector) + TF-IDF retrieval, relevance floors. Built once per (csv, chroma) via `_cached_engine` lru_cache. |
| `embedding_utils.py` | `embed_texts()` → `SentenceTransformer("all-MiniLM-L6-v2")`, lru_cached singleton. **Local CPU inference.** |
| `analytics.py` | Box-score analytics: `load_box_scores`, `enrich_box_scores`, `rank_players`, `recommend_squad`, `answer_stat_query`, `COUNTING_COLUMNS`, `TEAM_ALIASES`. |
| `services/analytics_service.py` | `load_project_box_scores` (cached CSV load) + Phase 0 `box_scores_from_match_player_stats` (DB→DataFrame adapter). |
| `services/app_data_client.py` | `AppDataClient` — HTTP client to the .NET app. **Bare `requests.get` per call, no session reuse, 8s default timeout.** |
| `services/groq_client.py` | `GroqClient` — hosted LLM (Groq) for rewrite, classify, generation. Network-bound. |
| `services/prediction_service.py` | `PredictionService.load_predictions()` — per-team holdout table from the trained model (PDF box scores only). |
| `services/memory_store.py` | `MemoryStore` — sqlite chat history + Chroma semantic memory; `rewrite_followup_question`. |
| `services/team_task_queue.py` | `TeamTaskQueue` — in-process per-team FIFO daemon queue for ingest/retrain. |
| `services/project_store.py` | `ProjectStore` — all on-disk paths (csv, chroma dirs, sqlite). |
| `requirements.txt`, `Dockerfile` | Deps + container. |
| `test_*.py` | `test_routing_regression.py`, `test_rag_safety.py`, `test_phase0_data_unification.py`, `test_parser_routing.py`, `test_fastapi_microservice.py`. |

The .NET side (`SportsPlatform.Auth.Api/`) exposes the data endpoints the
`AppDataClient` calls (match-player-stats, match-reports, schedule, etc.) and owns
the `HttpClient` that calls this microservice from the app.

---

## Phase 0 — Data unification (DONE, recap)

Already implemented and statically reviewed:

- `box_scores_from_match_player_stats()` adapter — DB rows → analytics box-score
  DataFrame (made/attempted split, derived FG, cumulative-row drop, minutes/% enrich).
- `_project_box_scores()` provider — **prefer CSV, fall back to DB**, union only
  when CSV is non-empty but lacks the asked team.
- DB-backed lineup fallback (`_lineup_from_box_scores`) when no model is trained.
- DB-first match-report lane (`_looks_like_report` / `_answer_match_report`).
- `answer_stat_query(..., df=...)` now accepts a pre-built frame.
- Tests: `test_phase0_data_unification.py` + report routing cases.

**Known limitation carried into Phase 1:** the provider is a fallback, not a true
union; the DB rows are all tagged team `"EGY"` (hardcoded default); and there is no
deduplication, so a naive union would double-count games that exist in both planes.

---

## Phase 1 — Unified stats view (data correctness)

### Goal
Serve the chatbot **one clean row per player per game** with the PDF-vs-DB overlap
already resolved — and put that resolution **where the data lives (.NET/PostgreSQL)**,
not in a pandas pipeline inside the chatbot. Also stop hardcoding the team code.

### Why — and why the union moves to .NET
PDF box scores are **imported into the DB** (`AppDataClient.get_match_player_stats`
docstring: "PDF-imported into the DB"). So the CSV (PDF-derived) and the DB overlap
heavily — often the *same* games. The Phase 0 provider dodges double-counting by only
ever using one source at a time (a fallback, not a union).

The naive next step — `pd.concat` the two frames and `drop_duplicates` on a dedup key
inside `analytics_service` — is **shadow-ETL**: it reimplements in pandas a join the
database can already do, and it does it on every request, with a key the chatbot can
only guess at. The database can *see* both planes (PDF-imported rows and coach-entered
rows are the same tables) and can resolve the overlap deterministically.

**Decision (from review): the union/dedup belongs in the .NET/PostgreSQL layer.**
There is no technical limitation preventing it — .NET already owns the DB and is the
sole data gateway. A SQL/EF Core view (or a dedicated endpoint) defines "the
authoritative box score" once, in one place, tested in SQL, consistent for *every*
consumer. The chatbot then becomes a thin consumer: one HTTP call → reshape → done.
This **shrinks** Phase 1 rather than growing it, and removes the per-request dedup
cost and the double-counting risk entirely.

### Work item A — .NET: unified box-score view + endpoint (the real fix)
- **Where:** `SportsPlatform.Auth.Api/` (the .NET side).
- **Steps:**
  - Define a SQL/EF Core **view** (start with a plain view; only materialize if
    measured slow) that emits one row per `(player, game)` with a deterministic
    precedence rule for overlap, e.g. a coach-entered/`per_game` row with a real
    `game_no` wins over a PDF-imported row for the same `(player, opponent, game)`.
  - Expose it as `GET /projects/{id}/unified-box-scores` (or extend the existing
    match-player-stats endpoint with a `?deduplicated=true` flag), returning the same
    field names the adapter already consumes (`name`, `opponent_name`, `game_no`,
    `granularity`, `two_pt_ma`, …).
  - If the view is materialized, refresh it on PDF-import completion (the import path
    already exists on the .NET side); a plain view needs no refresh.
- **Why here:** dedup logic sits next to the data, is the same for the app and the
  chatbot, and is testable without Python.

### Work item B — Python: consume the unified view (thin adapter)
- **Files to change:**
  - `services/app_data_client.py` — add `get_unified_box_scores(team_id, ...)` calling
    the new endpoint; keep `get_match_player_stats` for the fallback path.
  - `services/question_service.py` — rewrite `_project_box_scores()`:
    - **Primary:** `box_scores_from_match_player_stats(client.get_unified_box_scores(...))`
      — already deduplicated upstream, so **no concat, no drop_duplicates** in Python.
    - **Fallback:** when the unified endpoint isn't deployed (fail-soft None), keep the
      Phase 0 CSV-or-DB fallback. The pandas union survives **only** as this fallback,
      gated by `BOX_SCORE_SOURCE` (below), not the default path.
    - Fix the docstring (it currently says "PDF ∪ DB" but described fallback).
  - `services/question_service.py` — add `_project_team_code(project_id)` resolving the
    real team abbreviation (via a new `AppDataClient.get_team` or project config),
    cached; thread it into `box_scores_from_match_player_stats(rows, team=...)` so
    `"EGY"` is no longer hardcoded in the runtime path.
  - `services/app_data_client.py` — add `get_team(team_id)` → `{code, name}` if the
    .NET app exposes it; otherwise infer from match data and document the limitation.

### Fallback-only union (kept, demoted)
- `services/analytics_service.py` — `box_scores_union(csv_df, db_df, *, key_cols)`
  (concat → drop_duplicates on a normalized `(player, opponent, game_no/date)` key,
  source-priority `keep="first"`). **Only invoked when the unified endpoint is absent.**
- Reachable via `BOX_SCORE_SOURCE = unified | db_first | csv_first | union`
  (default `unified`; the others are the offline/fallback ladder).

### New env vars
- `BOX_SCORE_SOURCE` = `unified` (default) | `db_first` | `csv_first` | `union`
- `UNIFIED_BOX_SCORES_ENDPOINT` (optional override of the .NET route)

### Tests
- **.NET:** a SQL test that a game present in both planes yields exactly one row, and
  precedence picks the coach-entered values.
- **Python (`test_phase0_data_unification.py` + `test_box_score_union.py`):**
  - adapter consumes a unified-endpoint payload unchanged (already deduped → counts
    are correct without any Python dedup).
  - fallback `box_scores_union`: CSV(team A) + DB(team A, same game) → one row;
    CSV(A)+DB(B) → both retained; collision resolution honors source priority.
  - team-code resolution: a non-Egypt project tags rows with its real code and
    `rank_players(team=...)` still matches.

### Risks
- The .NET view is the long pole (DB work, deploy). Mitigation: it is **additive** —
  ship the Python `get_unified_box_scores` consumer behind `BOX_SCORE_SOURCE=unified`,
  but keep `db_first` as the safe default until the endpoint is live, then flip.
- View precedence rule wrong → silently drops good rows. Mitigation: validate the
  SQL row counts against known games before flipping the default.

### Acceptance criteria
- A team with overlapping PDF + DB data shows **correct, non-doubled** season totals,
  with the dedup performed **once in SQL**, not per-request in pandas.
- No "I couldn't find matching box score rows" for any team with data in either plane.
- No hardcoded `"EGY"` remains in the runtime path.
- The Python request path does **no** `drop_duplicates` on the default (`unified`) path.

---

## Phase 2 — Latency (local and remote)

### Goal
Cut **time-to-first-token** dramatically and reduce total latency, on both the local
single-machine setup and a networked/multi-machine deployment.

### Diagnosis (grounded in the current hot path)
A single `/ask` call runs **synchronously with no streaming**, so the user stares at
a blank screen until the whole chain finishes. In the worst case that chain does, in
series:
- up to **3 hosted-LLM round trips** — Groq follow-up rewrite
  (`_rewrite_followup_with_groq`), Groq classifier
  (`_classify_question_with_groq_first`), and the final Groq generation;
- **1–2 local embedding passes** — `all-MiniLM-L6-v2` on CPU for semantic memory and
  Chroma retrieval;
- **N sequential HTTP calls** to the .NET app, each a **fresh `requests.get`** (new
  TCP+TLS handshake every time), default **8s** timeout.

**Local vs different machine:** Groq is a remote API → roughly constant regardless of
machine (won't 10×). The embedding model is **local CPU** → this is what slows down
on a weaker box. The .NET app + DB is on localhost now; **split it across a network
and each of those several sequential, un-pooled HTTP hops gains real round-trip
time** — that, plus a weaker CPU for embeddings, is the realistic source of the "10×"
fear. It is **not** primarily "querying the DB instead of having our own DB."

### Phase 2a — Streaming + async (biggest perceived win)
- **Files:** `api/main.py`, `services/question_service.py`, `services/groq_client.py`,
  and the .NET caller (`SportsPlatform.Auth.Api` chatbot client + the frontend).
- **Precondition (from review — do NOT skip):** today `/ask` is a **synchronous
  `def`**, so FastAPI runs it in the anyio threadpool and blocking `requests` /
  CPU-bound embedding work tie up a *worker thread*, not the event loop — which is
  fine. The moment a route becomes `async def`, that same blocking work runs **on the
  event loop** and stalls every concurrent request. So async streaming is only safe
  if, in the async path:
  - all outbound HTTP uses `httpx.AsyncClient` (not `requests`);
  - every CPU-bound or blocking call (MiniLM embeddings, TF-IDF, pandas, the sync
    `AppDataClient`) is wrapped in `await asyncio.to_thread(...)` / `run_in_threadpool`;
  - fan-out uses `asyncio.gather(..., return_exceptions=True)` **and** each offloaded
    call is wrapped to return a fail-soft sentinel (None) on error — `to_thread`
    exceptions otherwise surface only when awaited and can either 500 the request or,
    in a careless `gather`, cancel siblings. Preserve the existing "return None →
    caller falls through" contract.
- **Steps:**
  - Add a streaming route `POST /projects/{id}/ask/stream` returning
    `StreamingResponse` (SSE / `text/event-stream`).
  - Add `GroqClient.stream_text()` yielding token chunks (Groq supports streamed
    chat completions).
  - Add `QuestionService.ask_stream()` that does all routing/retrieval, then yields
    the final answer tokens as they arrive; non-LLM lanes (DB answers) stream the
    final string in one or few chunks.
  - .NET: proxy the SSE stream through to the client (or call the stream endpoint
    directly from the frontend); render tokens as they land.
- **Result:** first token in ~1s instead of a blank screen for the full chain.

### Phase 2g — Bounded follow-up context window (LLM memory)
- **File:** `services/question_service.py` (`_rewrite_followup_with_groq`),
  `services/memory_store.py`.
- **Why:** the follow-up rewrite feeds chat history to Groq. Unbounded, it grows every
  turn — more tokens, more latency, and eventually window pressure. (Lower stakes than
  2a–2c, but it's free latency on long sessions.)
- **Steps:**
  - Cap the history fed to the rewrite at the **last N turns** (`FOLLOWUP_HISTORY_TURNS`,
    default ~6).
  - Only summarize older turns if coaches actually run long multi-turn sessions —
    summarization adds an LLM call, so gate it (`FOLLOWUP_SUMMARIZE=0` by default) and
    revisit only if the rolling window proves too lossy.

### Phase 2b — Remove redundant LLM round trips
- **File:** `services/question_service.py`.
- **Steps:**
  - When a deterministic hybrid lane matches (the `_classify_lane` regex routing),
    **skip the Groq classifier entirely**.
  - Only call `_rewrite_followup_with_groq` when the deterministic rewrite
    (`deterministic_rewrite` flag) did **not** already resolve the follow-up.
  - Gate any "LLM answer formatting" (`ENABLE_ANALYTICS_LLM_FORMATTING`) off by
    default for DB answers — they're already structured.
- **Result:** removes ~1–2 network round trips (~0.3–1s each) from the common path.

### Phase 2c — Connection pooling + tighter timeouts (the real "DB" fix)
- **File:** `services/app_data_client.py`.
- **Steps:**
  - Replace bare `requests.get` with a module-level `requests.Session()` (HTTP
    keep-alive + connection pool) so repeated calls skip the TCP+TLS handshake.
  - Split timeouts: a short **interactive** timeout (`APP_API_TIMEOUT_INTERACTIVE`,
    default 2–3s) for lane calls during `/ask`, vs the existing 8s for background
    ingest.
  - Add a `(connect, read)` timeout tuple, not a single scalar.
- **Result:** the largest win on a networked deployment — eliminates per-call
  handshake latency across the several sequential hops.

### Phase 2d — Parallelize independent work
- **File:** `services/question_service.py`.
- **Steps:**
  - Run the independent pre-routing steps concurrently: semantic memory retrieval,
    box-score load (`_project_box_scores`), and DB availability fetch
    (`get_unavailable_names`). Use a `ThreadPoolExecutor` (the handler is sync) or
    move to `async` + `asyncio.gather` (pairs naturally with 2a).
  - Prefetch the lane's DB call in parallel with classification where the lane is
    already known from regex.

### Phase 2e — Warm singletons at startup
- **Files:** `api/main.py`, `embedding_utils.py`, `rag_engine.py`.
- **Steps:**
  - On FastAPI `startup`, call `get_embedding_model()` once and warm a trivial
    `embed_texts(["warmup"])` so the first **user** request doesn't pay the cold
    model load. Optionally warm a Chroma client handle.
- **Result:** removes a multi-second cold start from the first query after boot.

### Phase 2f — Short-TTL read cache (the "own DB" idea, done right)
- **Files:** `services/app_data_client.py` (or a small `services/cache.py`).
- **Steps:**
  - Wrap `get_match_player_stats`, `get_match_reports`, `get_availability`,
    `get_schedule` in a per-process TTL cache (e.g. 30–60s, keyed by
    `(team_id, endpoint, params)`).
  - Invalidate on ingest/rebuild (the `TeamTaskQueue` already serializes those).
- **Result:** a multi-message conversation stops re-hitting the .NET API every turn.
  This is the pragmatic version of "have our own DB" — **cache reads, don't stand up
  a second database** (which would add sync complexity for no latency benefit here).

### New env vars
- `APP_API_TIMEOUT_INTERACTIVE` (default `3`), keep `APP_API_TIMEOUT` (8) for ingest.
- `APP_API_CACHE_TTL` (default `45`).
- `SKIP_GROQ_CLASSIFIER_ON_LANE_MATCH` (default `1`).
- `WARM_MODELS_ON_STARTUP` (default `1`).
- `FOLLOWUP_HISTORY_TURNS` (default `6`), `FOLLOWUP_SUMMARIZE` (default `0`).

### Tests
- **Add `test_latency_instrumentation.py`** (see Phase 2-pre below) and unit tests:
  - classifier is **not** called when a lane matches (assert via a fake GroqClient
    call counter, mirroring `test_fastapi_microservice.py`'s `FakeGroqClient`).
  - `AppDataClient` reuses a single `Session` (assert the session object identity).
  - TTL cache returns the cached payload within the window and refetches after.
  - streaming endpoint yields more than one chunk and the concatenation equals the
    non-streamed answer for a deterministic (DB) lane.

### Phase 2-pre — Measure first (do this before 2a–2f)
- **File:** `services/question_service.py` (+ a tiny `services/timing.py`).
- **Steps:** add lightweight per-stage timing (rewrite ms / classify ms / each
  lane's DB ms / retrieval ms / final-LLM ms), emitted to logs and optionally
  returned in the response under a `timings` debug field (gated by `DEBUG_TIMINGS`).
- **Why:** confirm the real bottleneck on *your* machine before optimizing; keeps the
  later phases honest.

### Acceptance criteria
- First visible token < ~1.5s on the local box for an LLM answer; DB answers < ~0.8s.
- Common-path LLM round trips reduced from up to 3 → 1.
- On a simulated remote deployment (added latency to the .NET host), total time grows
  roughly linearly with **one** RTT, not N (proves pooling + parallelism + cache).

---

## Phase 2.5 — Concurrency & deployment hardening

### Goal
Close the seams that don't show up on a single dev process but bite the moment we add
async/streaming (Phase 2a) or run more than one worker/instance. These are
preconditions for trusting Phases 2 and 5 in production, grouped here so they aren't
forgotten.

### 2.5a — Async event-loop safety (pairs with 2a)
Covered as the **precondition** in Phase 2a: `httpx.AsyncClient` for HTTP,
`asyncio.to_thread` for embeddings/TF-IDF/pandas/sync clients, and
`gather(..., return_exceptions=True)` with per-call fail-soft sentinels. Restated here
because it is a *hardening* requirement, not just a feature detail: shipping 2a without
it converts today's safe-but-slow blocking into a loop-stalling outage under
concurrency.

### 2.5b — Cross-worker cache & queue coherence
- **Problem:** the Phase 2f TTL cache, the `_cached_engine` lru_cache, and the
  embedding-model singleton are **per-process**. Run Uvicorn/Gunicorn with >1 worker
  and each worker has its own copy — a rebuild in worker A leaves workers B/C serving
  stale vectors/CSVs. `TeamTaskQueue` is likewise **in-process**: per-team
  serialization silently breaks across workers (two workers can retrain the same team
  concurrently).
- **Steps (short of full Phase 5):**
  - Adopt a **shared version stamp** per team (a small file or DB row bumped on every
    ingest/rebuild). Caches key on it, so a bump in one worker invalidates all workers'
    reads on next access without cross-process messaging.
  - Until Phase 5's distributed queue lands, **either** pin ingest/retrain to a single
    worker (a dedicated process / `--workers 1` for the ingest route) **or** guard
    retrain with a cross-process lock (file lock / DB advisory lock) keyed by team.
  - Document explicitly: the in-process queue is correct only at one worker; >1 worker
    requires Phase 5 or the lock above.

### 2.5c — Startup readiness gating (no crash-loop, no cold first hit)
- **Problem:** warming models on startup (Phase 2e) can fail (missing model, OOM); if
  warmup throws in `lifespan`/`startup`, the process can crash-loop. Conversely, if the
  health probe returns ready *before* warmup finishes, the first real request still
  pays the cold load.
- **Steps:**
  - Use a FastAPI `lifespan` that warms models inside a try/except — log and degrade,
    never crash the process on warmup failure.
  - Split probes: `/health` (liveness, always cheap) vs `/ready` (readiness, flips true
    only after warmup completes). Point the load balancer/orchestrator at `/ready`.

### 2.5d — File-write races on ingest vs read
- **Problem:** ingest rewrites the box-score CSV and the Chroma dir while `/ask`
  requests read them. A half-written CSV or a mid-rebuild Chroma dir yields garbage or
  errors.
- **Steps:**
  - CSV: write to a temp file, then **atomic `os.replace`** into place.
  - Chroma: build into a **versioned/sidecar directory** and flip a pointer (or use the
    version stamp from 2.5b) rather than mutating the live dir in place.
  - SQLite (chat history/memory): ensure **WAL mode** so readers don't block the writer.

### 2.5e — Chroma incremental (hashed) re-embedding
- **Problem:** rebuild currently re-embeds the **whole** corpus because nothing tracks
  what changed — on a weak CPU, MiniLM re-embedding is the dominant ingest cost. (This
  is the same root issue as 2.5d's full-dir rewrite, viewed from the CPU side.)
- **Files:** `rag_engine.py`, the ingest path.
- **Steps:**
  - Hash each chunk's text (e.g. SHA-1 of normalized content); store the hash alongside
    the vector / as Chroma metadata.
  - On rebuild, embed only chunks whose hash is **new or changed**; `upsert` those by
    ID and `delete` by ID the chunks whose source content disappeared.
  - Full rebuild ("embed everything") drops to "embed the delta" — the common edit
    (one new PDF) re-embeds only that PDF's chunks.
- **Acceptance:** re-ingesting an unchanged corpus performs **zero** new embeddings;
  adding one document embeds only that document's chunks.

### 2.5f — Request-path DataFrame memory
- **Problem:** `_project_box_scores` builds a pandas frame per request; under
  concurrency that's N frames live at once. Small per team today, but unbounded as
  rosters/seasons grow and as workers multiply.
- **Steps:**
  - Cache the per-team enriched frame keyed by the 2.5b version stamp (build once per
    data version, not per request); serve a read-only view to handlers.
  - Keep columns to the analytics schema only; avoid copying the frame per lane.

### Acceptance criteria (phase)
- Two workers serve coherent data after a rebuild in one of them (no stale-vector
  window beyond one version-stamp check).
- Warmup failure degrades gracefully; `/ready` gates traffic correctly.
- Concurrent ingest + read never observes a partial CSV or mid-rebuild index.
- Re-ingest of an unchanged corpus does no embedding work.

---

## Phase 3 — Local NLP intent classifier + NER (postponed)

### Goal
Replace/augment the regex `_looks_like_*` routing and the Groq classifier with a
**local** intent classifier and named-entity recognizer, so routing is faster,
cheaper, and works offline — keeping the LLM only for generation.

### Why
Routing currently leans on regex (good, deterministic) with a Groq fallback (network
cost). A small local model removes the classifier round trip (ties into Phase 2b) and
extracts entities (opponent, player, metric, date window) more robustly than regex.

### Options & tradeoffs
- **spaCy (`en_core_web_sm` / `_trf`)** for NER + a lightweight text-cat — pros: easy,
  CPU-friendly, mature; cons: generic NER needs a custom entity ruler for team/player
  names.
- **Fine-tuned MiniLM/DistilBERT intent classifier** (sentence-transformers head) —
  pros: high routing accuracy, reuses the embedding model already loaded; cons: needs
  a labeled intent dataset.
- **Zero-shot (e.g. `bart-large-mnli`)** — pros: no training data; cons: heavier,
  slower on CPU.
- **Recommendation:** spaCy `EntityRuler` seeded from the roster/opponents (from the
  DB) for NER, plus an embedding-based nearest-centroid intent classifier built on
  the existing `all-MiniLM` vectors (cheap, no new heavy model).

### Files to add
- `services/intent_classifier.py` — local intent model: load, `classify(question) ->
  (lane, confidence)`; falls back to the existing regex `_classify_lane` below a
  confidence threshold.
- `services/entity_extractor.py` — spaCy pipeline + `EntityRuler` seeded from
  `AppDataClient.get_roster` / opponents; returns `{opponents, players, metric,
  window}`.
- `data/intents/` — seed phrases per lane for the centroid classifier.

### Files to change
- `services/question_service.py` — call the local classifier first; keep regex as a
  tie-breaker and the Groq classifier as a last resort (or remove it once accuracy is
  proven). Feed extracted entities into the lane handlers (opponent guard, player
  filters) instead of re-parsing.
- `services/rag_service.py` — use the extracted opponents directly in
  `filter_chunks_by_opponent` instead of the regex `_question_opponents`.
- `requirements.txt` — add `spacy` (+ model download in `Dockerfile`).
- `Dockerfile` — `python -m spacy download en_core_web_sm`.

### Tests
- Extend `test_routing_regression.py`: the local classifier must pass the same
  `BUG_CASES`/`LANE_CASES` as the regex router (it should never regress routing).
- New `test_entity_extractor.py`: opponent/player/metric extraction on the known
  phrasings, including the Angola/Mali cases.

### Risks
- Model size / cold start (mitigated by Phase 2e warmup).
- NER false positives on player names that collide with common words — mitigate with
  the DB-seeded `EntityRuler` (closed vocabulary) over generic NER.

### Acceptance criteria
- Routing accuracy ≥ the regex baseline on the test suite, with the Groq classifier
  call eliminated from the common path.

---

## Phase 4 — Vision + web search (Gemini / Grok), postponed

### Goal
Add (a) **image/PDF-page vision analysis** (read a box-score image, a whiteboard
photo, a screenshot) and (b) **grounded web search** (latest opponent news, FIBA
results) — via a hosted multimodal model, with a clear local-vs-API tradeoff.

### Provider notes (verify pricing/limits at build time — these move)
- **Gemini:** vision is available on the Flash tier with a free daily quota; **Search
  grounding is billed per grounded result**; some Pro tiers left the free tier in
  2026. Good default for vision + grounded search behind one API.
- **Grok (xAI):** vision (jpg/png) + **live web/X search** billed per source;
  promotional monthly credits. Strong for real-time/social signal.
- **Local alternative:** an open VLM (e.g. a LLaVA-class model) for vision — pros:
  no per-call cost, data stays local; cons: heavy GPU/CPU, slower, lower quality, and
  **no web search** (you'd still need a search API). Recommend hosted for vision +
  search, local only if data-residency requires it.

### Files to add
- `services/vision_client.py` — provider-agnostic interface
  (`analyze_image(path, prompt)`); Gemini and Grok backends behind a factory; env-gated
  and **fail-soft** (returns None when unconfigured, like `GroqClient`/`AppDataClient`).
- `services/web_search_client.py` — `search(query) -> [results]` with grounding
  citations; same fail-soft pattern.
- `services/multimodal_router.py` (optional) — detect image attachments / "search
  the web for…" intents and dispatch.

### Files to change
- `api/main.py` — accept image uploads on the ask/session-upload routes; pass image
  refs into `ask()`. A new `pdf_scope`-style flag for "use web search".
- `services/question_service.py` — two new lanes: `vision` (when an image is
  attached) and `web_search` (when the question needs current external facts and the
  PDFs/DB can't answer). Both **after** the internal lanes so local data wins.
- `requirements.txt` — `google-generativeai` and/or `xai`/HTTP client.
- `.env` / settings — `GEMINI_API_KEY`, `GROK_API_KEY`, `VISION_PROVIDER`,
  `WEBSEARCH_PROVIDER`, `ENABLE_WEBSEARCH`, `ENABLE_VISION`.

### Tests
- `test_vision_client.py` / `test_web_search_client.py` with mocked provider
  responses (no live keys in CI); assert fail-soft when unconfigured.
- Routing: an attached image routes to the `vision` lane; "latest news on <opponent>"
  routes to `web_search` only when internal data can't answer.

### Risks
- Cost blowout on search grounding — add a per-day call cap + caching of search
  results.
- Privacy: sending team images to a third-party API — gate behind explicit config and
  document it.

### Acceptance criteria
- With keys configured, an uploaded box-score image yields structured stats; a
  current-events question returns a cited answer. With keys absent, both lanes are
  silent no-ops (no errors, falls through to existing behavior).

---

## Phase 5 — Scale-out (Celery/Redis), postponed until multi-instance

### Goal
Support running **multiple service instances** without breaking per-team job
serialization or in-process caches.

### Why (and why later)
`TeamTaskQueue` is an **in-process** per-team FIFO (its own docstring says: swap for
Redis/RQ or Celery keyed by team when scaling horizontally). It is correct for a
**single** instance. The Phase 2f TTL cache is also per-process. Both break across
processes. Don't pay this complexity until you actually run >1 instance.

### Files to change / add
- `services/team_task_queue.py` — extract an interface; add a `CeleryTeamQueue` /
  `RedisRQTeamQueue` backend keyed by team so serialization holds across processes.
- `services/cache.py` — swap the per-process TTL cache for Redis when
  `CACHE_BACKEND=redis`.
- `api/main.py` — webhook/ingest enqueues to the distributed queue.
- New `worker.py` / `celery_app.py` — worker entrypoint.
- `requirements.txt` — `celery[redis]` or `rq` + `redis`.
- `Dockerfile` / compose — a Redis service + a worker container.

### Streaming vs Celery (decision already reached)
For **interactive chat**, prefer **streaming** (Phase 2a) over offloading to Celery —
the user wants tokens now, not a job ticket. Celery/RQ is for **background** ingest/
retrain and only earns its keep at multi-instance scale. Keep chat on the request
path (async + streaming); push heavy ingest to the queue.

### Acceptance criteria
- Two instances behind a load balancer process two jobs for the **same** team
  strictly in order; jobs for different teams run in parallel; caches stay coherent.

### Status: implemented
- `services/team_task_queue.py` — `TeamQueue` ABC; `InProcessTeamQueue` (default,
  `TeamTaskQueue` kept as alias); `make_team_queue()` factory on `QUEUE_BACKEND`.
- `services/task_runner.py` — broker-free `run_ingest_and_retrain(payload_dict)` body
  shared by both backends (rebuilds services from the lru_cached factories, so a Celery
  worker process can run it without the request's objects).
- `services/celery_app.py`, `services/tasks.py`, `services/celery_queue.py` —
  Celery app + registered `chatbot.run_team_job` (per-team Redis lock) + `CeleryTeamQueue`.
- `services/redis_client.py`, `services/redis_lock.py` — fail-soft shared client +
  per-team lock (`SET NX PX` + Lua fenced release; no-op lock when Redis absent).
- `services/cache.py` — `RedisTTLCache` + `make_read_cache()`; `get/bump_team_version`
  use a Redis `INCR` counter under `CACHE_BACKEND=redis`, else the host-local file.
- `api/main.py` webhook now calls `queue.enqueue_ingest(payload)` (backend-agnostic);
  `api/dependencies.py` exposes `get_team_queue` (`get_team_task_queue` alias kept).
- Infra: `requirements.txt` += `celery[redis]`, `redis`; `worker.py` entrypoint;
  `docker-compose.yml` adds `redis` + `fiba-chatbot-worker` (scale with
  `--scale fiba-chatbot-worker=N`); existing `Dockerfile` serves both roles.
- Tests: `test_scale_out.py` (broker-free) covers the factory, in-process
  serialization/parallelism, `enqueue_ingest` wiring, payload serialization, and the
  Redis fall-soft paths.

**Env vars:** `QUEUE_BACKEND` (`inprocess`|`celery`), `CACHE_BACKEND` (`redis` to
enable), `REDIS_URL`, `CELERY_BROKER_URL`, `CELERY_RESULT_BACKEND`, `TEAM_LOCK_TTL`
(default 900s), `TEAM_JOB_RETRY_SECONDS` (default 5s), `CELERY_TASK_QUEUE`. Absent these,
the service runs exactly as before (in-process queue + per-process cache).

**Serialization note:** the Redis lock guarantees *mutual exclusion* per team across
processes (no two same-team jobs overlap — the property correctness needs). Strict
submission-order FIFO follows broker delivery + retry-on-contention; it is not a total
order guarantee under adversarial redelivery, which is acceptable for ingest/retrain.

---

## Suggested execution order

1. **Phase 2-pre** (measure) — cheap, informs everything.
2. **Phase 1** (data correctness) — ship the Python consumer behind
   `BOX_SCORE_SOURCE=db_first` immediately; land the .NET unified view in parallel,
   then flip the default to `unified` once SQL row counts validate. The pandas union
   stays only as the offline fallback.
3. **Phase 2a–2g + Phase 2.5** (latency **and** hardening) — treat these as one block:
   streaming/async (2a) MUST ship with its 2.5a event-loop safety; pooling/cache
   (2c/2f) MUST ship with 2.5b coherence + 2.5d atomic writes. Do not land the
   performance half without the hardening half.
4. **Phase 3** (local NLP) — removes the classifier round trip; depends on 2e warmup.
5. **Phase 4** (vision + search) — additive lanes; independent of 3.
6. **Phase 5** (scale-out) — only when moving to multiple instances; subsumes the
   interim single-worker/lock guard from 2.5b.

## Cross-cutting conventions to preserve
- **Push data work to the data layer.** Joins, dedup, and aggregation belong in
  PostgreSQL/.NET (which owns both planes), not in per-request pandas. The chatbot is a
  consumer of clean data, not a shadow-ETL.
- Every external client stays **env-gated and fail-soft** (returns None → caller
  falls through), matching `GroqClient` / `AppDataClient`. This contract must survive
  the async migration: `to_thread`/`gather` results are wrapped so a failure becomes a
  None fall-through, never an unhandled 500 or a cancelled sibling.
- New lanes go **after** internal-data lanes so local PDF/DB answers win over external
  calls.
- **Performance change ⇒ matching hardening change.** Async without 2.5a, multi-worker
  caching without 2.5b, or ingest speedups without 2.5d are net regressions.
- Every behavioral change gets a regression test in the existing `test_*.py` style
  (script entry point + pytest functions), since the sandbox can't always run them —
  the suite is the safety net.
