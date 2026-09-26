# Integration Plan — Chatbot + Prediction Model (FIBA)

> Status: design / decisions agreed. Stack specifics marked **[confirm from code]** still need a pass over the two repos (the sandbox was unavailable when this was written).

---

## 1. Goal

One unified flow:

1. An analyst uploads a game's PDFs (game stats → player stats).
2. The PDFs are stored **raw (as blobs)** in the database for provenance and re-extraction.
3. The same upload is **extracted once** into a structured schema + vector embeddings.
4. The **chatbot** answers questions off that data, scoped to the **active team** from the team selector.
5. When the user asks for a **suggestion**, the chatbot calls the **prediction model**, which leads; the chatbot only explains the result.
6. Conversations are **persisted, re-readable, searchable**, and prior turns are fed back as context.

---

## 2. Agreed decisions

| Topic | Decision |
|---|---|
| Data path | **Hybrid router** — structured DB is source of truth; RAG/vector is fallback for open-ended questions |
| PDF extraction | Tolerant parsing (templates mostly consistent, some variation) + a validation step |
| Team scope | Account owns multiple teams; an **active team selector** scopes every query |
| Suggestions | **Model leads, bot explains** |
| Model integration | Model outputs CSV → wrapped so the chatbot consumes it as a tool result (not connected yet) |
| Chat history | Searchable persisted sessions + context memory |
| Roles | None — access gated by who has the chatbot button; everyone in = analyst |
| Injuries / doctor | Schema-ready, not built (no data source yet) |

---

## 3. Target architecture

```
                    ┌─────────────────────────────┐
   Analyst upload → │   Ingestion pipeline         │
   (game/player     │   1. store raw PDF (blob)    │
    stats PDFs)     │   2. extract → structured    │
                    │   3. validate                │
                    │   4. embed → vector store    │
                    └───────────────┬──────────────┘
                                    │
        ┌───────────────────────────┼───────────────────────────┐
        ▼                           ▼                           ▼
 ┌─────────────┐           ┌────────────────┐          ┌────────────────┐
 │ Raw PDF      │           │ Structured DB   │          │ Vector store   │
 │ (blobs)      │           │ players, stats, │          │ (chunks +      │
 │ provenance   │           │ lineups, games, │          │  embeddings)   │
 │ re-extract   │           │ (injuries: TBD) │          │                │
 └─────────────┘           └────────┬────────┘          └───────┬────────┘
                                    │                           │
                          ┌─────────┴───────────────────────────┴────────┐
                          │              CHATBOT (router)                  │
   Team selector ───────► │  classify intent → choose source              │
                          │   • facts/aggregates → Structured DB           │
                          │   • open-ended/narrative → Vector RAG          │
                          │   • suggestion/prediction → Prediction model   │
                          │  every query filtered by active team_id        │
                          └───────────────────┬───────────────────────────┘
                                              │
                                  ┌───────────┴───────────┐
                                  ▼                       ▼
                          ┌───────────────┐      ┌─────────────────┐
                          │ Prediction     │      │ Chat history    │
                          │ model (CSV out)│      │ sessions +      │
                          │ "leads"        │      │ context memory  │
                          └───────────────┘      └─────────────────┘
```

The raw-PDF-vs-DB tension is resolved by **storing the raw PDF but answering from the structured DB**. We do not re-parse a PDF with the LLM per question — it's slow, costly, and least reliable on exactly the numeric questions this domain is full of.

---

## 4. Data model (structured DB)

Proposed core tables (names indicative — align to existing schema **[confirm from code]**):

- **teams** — `team_id`, `name`, …
- **account_teams** — maps a user/account to the teams they can select (drives the selector).
- **games** — `game_id`, `team_id`, `opponent`, `date`, `source_pdf_id`, …
- **players** — `player_id`, `team_id`, `name`, …
- **player_game_stats** — `game_id`, `player_id`, points, rebounds, assists, minutes, DNP flag, … (the granular stats from each PDF).
- **raw_documents** — `doc_id`, `team_id`, `game_id`, `pdf_blob`/path, `uploaded_by`, `uploaded_at`, `extraction_status`.
- **predictions** — `prediction_id`, `game_id`, `team_id`, model CSV output parsed into rows, `created_at`.
- **injuries** *(schema only, not populated yet)* — `player_id`, `team_id`, `injury`, `date`, `active` (bool). Enables "count injuries per player, return max + active flag" later.

Every fact table carries `team_id` so the selector can filter with a single predicate, and so the same data feeds the prediction model.

---

## 5. Ingestion pipeline

On upload (one job per uploaded PDF):

1. **Store raw** — write the PDF blob to `raw_documents` with `team_id`, `game_id`, status = `pending`.
2. **Extract** — tolerant parser (templates mostly consistent). Pull game + per-player stat rows into the structured tables. Because formats vary somewhat, use rule-based extraction first, LLM-assisted fallback for off-template pages.
3. **Validate** — sanity checks (totals reconcile, player counts, no nulls in key fields). On failure → mark `needs_review`, surface to the analyst rather than silently storing bad numbers.
4. **Embed** — chunk the text, write embeddings to the vector store with `team_id` + `game_id` metadata for scoped retrieval.

This is the single most important reliability investment — every downstream answer and prediction depends on extraction being correct.

---

## 6. The router (chatbot core)

For each incoming question (with the active `team_id`):

1. **Classify intent**:
   - **Factual / aggregate** ("top scorer last game", "average rebounds", later "most injured player") → query **Structured DB**.
   - **Open-ended / narrative** ("how did we play in the 3rd quarter", "summarize the game") → **Vector RAG**.
   - **Suggestion / prediction** ("who should start", "what's our predicted result", "lineup suggestion") → **Prediction model**.
2. **Always inject `team_id`** as a hard filter on whichever source is used.
3. **Compose the answer** with the LLM, citing the underlying rows/chunks where possible.

Start with a lightweight classifier (few-shot intent prompt or keyword+LLM hybrid); it can be hardened later.

---

## 7. Prediction-model integration ("model leads, bot explains")

Current state: model outputs a CSV; chatbot can read CSV; not yet wired.

Plan:

1. **Wrap** the model so it can be invoked on demand for `(team_id, game_id)` and returns its CSV. **[confirm from code]** whether it's a script, importable function, or service — that determines whether we call it in-process or as a subprocess/service.
2. **Register it as a router tool**. When intent = suggestion, the router calls the wrapper.
3. **Parse the CSV** into structured prediction rows (also persisted to `predictions`).
4. **LLM explains, doesn't overrule** — the model's numbers are authoritative; the chatbot wraps them in plain language and context. No blending that could change the recommendation.
5. **Scope** — model only runs on the active team's data.

---

## 8. Chat history (full treatment)

- **Sessions** table: `session_id`, `user`, `team_id`, `created_at`, `title`.
- **Messages** table: `message_id`, `session_id`, role, content, `source` (db/rag/prediction), `created_at`.
- **Re-readable**: list past sessions, reopen, scroll the full transcript.
- **Searchable**: text search over message content (and optionally embed messages for semantic search).
- **Context memory**: feed the recent turns of the active session back into each new answer; optionally retrieve relevant past messages across sessions.

---

## 9. Team selector

- UI control listing the account's teams (from `account_teams`).
- Selected `team_id` is attached to every chatbot request, every retrieval, and every model call.
- History sessions are tagged with the team they were held under.

---

## 10. Phased implementation

**Phase 0 — Code audit [confirm from code]**
Read both repos: confirm vector DB (Chroma/FAISS/pgvector?), whether a relational DB already exists, LLM provider, and the prediction model's exact interface + required input columns.

**Phase 1 — Unified storage + ingestion**
Raw-PDF blob storage, structured schema, extraction + validation, embeddings with team/game metadata.

**Phase 2 — Team scoping + router**
Team selector end-to-end; intent router; DB-query and RAG paths working with `team_id` filtering.

**Phase 3 — Prediction model as a tool**
Wrap the model, parse CSV → `predictions`, "model leads, bot explains" suggestion path.

**Phase 4 — Chat history**
Sessions + messages, re-read, search, context memory.

**Phase 5 — Hardening / future**
Injuries table population + the doctor "most injured" query when a data source exists; extraction-failure review UI; eval of router accuracy.

---

## 11. Open items / dependencies

- **[confirm from code]** Vector DB, relational DB presence, LLM provider, embedding model.
- **[confirm from code]** Prediction model interface and required input columns; how a game/team keys its output.
- Decide where raw PDFs live (DB blob vs object storage + DB pointer) for size/performance.
- Extraction validation thresholds and the analyst review flow for `needs_review` uploads.
