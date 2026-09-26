# Integration Plan — Chatbot + Prediction Model as a Microservice

_Last updated: 2026-06-11 (revised after architecture review against the .NET app)_

## 0. Revision note — what changed after reviewing the actual codebase

This revision reconciles the plan with how `SportsPlatform.Auth` is actually built. Key corrections:

- **Storage:** the app stores the raw PDF on disk (`wwwroot/uploads/match-stats/{eventId}`) and keeps only the *path + metadata + extracted text + structured rows* in Postgres — it does not store the PDF bytes in Postgres. The existing `DownloadRawPdf` endpoint means the webhook can hand the microservice **signed URLs to pull**, not bytes.
- **Multi-sport:** `MatchStats` is multi-sport (it carries soccer fields alongside basketball). The model/chatbot are basketball-only, so the webhook must be **gated by sport** and must not fire for non-basketball matches.
- **Multi-PDF capture is a prerequisite (new Phase 0):** today the app stores **one** stats PDF per match (single `RawPdfPath`, replace semantics). "Forward all four PDF types" cannot work until the app can capture and label box-score / plus-minus / lineup / play-by-play. This is now Phase 0 and blocks everything else.
- **Extraction:** dropped "self-extract everything." The app already has a working box-score extractor (`extract.py` behind the FastAPI service on `:8100`). The app's **structured box score is canonical**; the microservice self-extracts **only** the three non-stats PDFs the app doesn't parse. One extractor per PDF type → no number divergence between the app UI and the chatbot.
- **Model state:** `fiba_clean_project/pipeline.py` holds the dataset and trained model in **module-level globals** (`MASTER_*`). These must become **per-team state persisted to disk** (joblib model + parquet frames). Retrain must run inside a **per-team serialized queue/lock** — concurrent webhooks for one team would otherwise race on shared globals (a correctness bug, not just a latency concern).
- **Tenant key:** `project_id = TeamId` (Guid). It passes `project_store`'s id regex unchanged; the per-project isolation already in `project_store.py` becomes per-team isolation directly.

## 1. Goal

Wire the existing RAG **chatbot** (`final chatbot/`) and the **FIBA prediction model** (`fiba_clean_project/`) together, and run them as a single **microservice on a separate machine** that plugs into your existing app. The flow you want:

1. An analyst uploads game files in **your app** (stats PDF → player stats, plus other PDF types).
2. Your app stores the raw PDF on disk and the structured rows + path/metadata in your Postgres, as it already does.
3. On every **basketball** upload, your app **forwards the raw PDFs (as signed pull-URLs)** to the microservice. Non-basketball uploads are not forwarded.
4. The microservice extracts them, **precomputes predictions**, and indexes them so the chatbot can answer questions about that game — scoped to the selected team.
5. A user can also upload a file **directly in chat**; the chatbot uses it for that conversation and stores it until the chat is deleted.
6. For **suggestion**-type questions, the chatbot **prioritizes the prediction model's output** over plain historical analytics.

## 2. Decisions locked in (from our conversation)

- **Deployment:** microservice on a different machine from the app.
- **Data access:** hybrid, with one extractor per PDF type. The app's **structured box score is canonical** — the microservice pulls box-score stats from the app's API/CSV rather than re-parsing them. The microservice **self-extracts only** the three non-stats PDFs (plus/minus, lineup, play-by-play) that the app does not parse. The app's **API** is also used for data that does not live in PDFs (injuries, roster, team/user metadata), enabling generalization to roles like a team doctor.
- **Model inputs:** the app forwards **all four PDF types** (box score, plus/minus, lineup, play-by-play) — which requires the Phase 0 multi-PDF capture. The microservice parses the three non-stats types with the prediction model's extractors and combines them with the canonical box score → **full feature set**.
- **Sync trigger:** **webhook/event push** from the app, **gated by sport** (basketball only), firing when a match's PDFs are ingested. Handled asynchronously through a **per-team serialized queue**.
- **Vector store:** keep the chatbot's existing **Chroma** exactly as it is (it already works). No migration to pgvector.
- **Relational store:** the microservice runs its **own isolated PostgreSQL instance** (separate from the app's Postgres, on the microservice side) for chat history, predictions, and extracted stats — replacing the current SQLite (`chat_history.db`). SQLite serializes writes and handles concurrency poorly, which is a bad fit for concurrent webhook ingestion + chat traffic; Postgres is already in your stack so there's no new technology to operate.
- **Team scope / tenant:** `project_id = TeamId` (the app's Guid). The app owns team selection/auth and passes `TeamId` on uploads and on each chat request; the microservice's existing per-project isolation (`project_store.py`) becomes per-team isolation directly. Because the project *is* the team, the separate `team` body param on `/ask` becomes redundant — set the project's canonical team code once at creation. "Team-scoped" = retrieved data is limited to that team.
- **Model persistence & concurrency:** the prediction model's `MASTER_*` module globals are replaced by **per-team state persisted to disk** (joblib for the model, parquet for the accumulated frames), loaded lazily per `project_id`. All ingestion/retrain for a given team runs in a **single-worker serialized queue** so concurrent webhooks cannot corrupt shared state. Retrain is triggered on **match-complete** (all expected PDF types present), not per file.
- **Chat history:** add a **readable** chat history (not just the semantic memory). In-chat uploaded files are stored with the session until the chat is deleted.

## 3. Architecture overview

**Your app (source of truth, unchanged extraction):** handles auth, team selection, normal uploads, stores stats PDFs raw + structured in Postgres, owns non-PDF data (injuries/roster/etc.).

**The microservice (this project):** owns the chatbot, the prediction model, its own Chroma index (unchanged), its own **isolated PostgreSQL instance** (chat history + predictions + extracted stats), and its own copy of forwarded PDFs. This Postgres is the microservice's alone — it is not the app's database. It is stateless toward the app except for: (a) receiving webhook pushes, and (b) calling the app's API for live non-PDF data.

```
          upload (analyst)
                |
            [ YOUR APP ]  --stores raw PDF + structured stats in its Postgres
                |
                |  webhook push: {game_id, team_id, all raw PDFs}
                v
        [ MICROSERVICE ] --(on demand)--> [ YOUR APP API ]  (injuries/roster/etc.)
          |  extract all 4 PDF types (existing chatbot + model extractors)
          |  build/refresh Chroma index + box-score store
          |  run prediction model -> store predicted EF per player
          v
     local stores: Chroma (as-is)  |  PostgreSQL [microservice's own] (chat history, predictions, extracted stats)
                |
            chat request {question, team_id, session_id, [optional uploaded file]}
                v
          chatbot answers (analytics | RAG | prediction-prioritized for suggestions)
```

## 4. Integration contract (app ↔ microservice)

This is the new surface you'll build/agree on:

**A. Upload webhook (app → microservice).** Fired after the app finishes ingesting an upload. Payload: a `game_id`/match identifier, the `team_id` it belongs to, and the raw PDF files (either attached, or URLs the microservice pulls). The microservice responds 202 and processes asynchronously (extract → index → predict). This maps cleanly onto the existing FastAPI `POST /projects/{project_id}/pdfs` + `rebuild` flow — `project_id` becomes your team/tenant key.

**B. Ask endpoint (app → microservice).** The app's chat UI calls `POST /projects/{team_id}/ask` with `{question, session_id, team, [file]}`. Already exists; we extend it to accept an optional in-chat file and to honor team scope strictly.

**C. App data API (microservice → app).** Read-only endpoints the microservice calls for non-PDF, possibly-live data (e.g. "current injured players for team Y"). Only needed for the generalization use cases; not required for core stats Q&A.

**D. Auth between machines.** A shared service token / mTLS so the webhook and the microservice→app calls are trusted. (Open item — see §10.)

## 5. End-to-end data flows

**Normal upload:** app ingests → webhook to microservice with team_id + all PDFs → microservice runs existing `extract_pdfs.py` (stats → `players_box_scores.csv` + chunks → Chroma) **and** the model's `extractors.py` for plus/minus, lineup, PBP → appends to the microservice's accumulated history → retrains/refreshes the model → writes predicted EF per player for that team/game into the predictions table. Chatbot is now ready to answer about that game.

**In-chat upload:** user attaches a PDF in a chat → microservice extracts it on the fly, scoped to that `session_id`, stores the file + extracted data tied to the session (deleted when the chat is deleted) → answers using it. If it's a stats game file, it can also be run through the prediction model for that conversation. It does **not** get pushed back into the app's permanent DB.

**Query:** app sends `{question, team_id, session_id}` → classifier (Groq, existing) decides intent + route → **analytics** (Pandas on the team's box-score data), **RAG** (Chroma retrieval + LLM), or **suggestion** (prediction-prioritized, §7). All reads filtered to `team_id`. Answer + sources persisted to readable chat history.

## 6. Changes needed inside the microservice

Most of the chatbot is reused unchanged. The work:

- **Tenant = team.** Use the existing per-project isolation (`services/project_store.py`) keyed by `team_id` so each team's PDFs, CSVs, Chroma index, and history are separate. The team selector value from the app drives this directly.
- **Accept the webhook** and forwarded PDFs; trigger extraction + index rebuild (reuse `POST /pdfs` + `rebuild`).
- **Wire in the prediction model.** Vendor `fiba_clean_project` into the microservice. On each upload, append the new game to an accumulated master dataset, call `retrain_model()` / `run_pipeline()`, and **persist** the trained model + predictions (the model currently lives only in memory — see §10).
- **Add the non-stats extractors** to the ingestion step so plus/minus, lineup, and PBP PDFs feed the model's full feature set.
- **Swap SQLite → microservice PostgreSQL.** Point `memory_store.py` (and the new predictions + extracted-stats tables) at the microservice's own isolated Postgres instead of `data/chat_history.db`. Chroma stays exactly as it is.
- **Readable chat history** (§8) and **in-chat file handling** (§5).
- **Suggestion routing** (§7).

## 7. Prediction model integration & "suggestion" prioritization

The model predicts **player efficiency (EF) for the upcoming game**, per player, given accumulated history. We store, per team, a table of `{player, predicted_EF, context, generated_at}` refreshed on every upload.

When the classifier detects a **suggestion** intent — today these are `squad_recommendation` and `player_opportunity_recommendation`, and we can extend the set — the chatbot answers **from the stored predictions first** (forward-looking projected EF and the squad/minutes recommendations derived from it), and uses the historical analytics engine only as a supporting/fallback layer. For non-suggestion questions ("how many points did X score") it stays on the existing analytics/RAG routes, which are backward-looking and correct as-is.

**Open item:** we should pin down exactly which question phrasings count as "suggestions" and how a predicted-EF list should be turned into a user-facing recommendation (starting five? who to give more minutes? matchup advice?). See §10.

## 8. Readable chat history

Today history is persisted in SQLite (`messages` table) and a Chroma `chat_memory` collection, but there's no way to read it back. We migrate the `messages` table (and the predictions + extracted-stats tables) to the microservice's own **PostgreSQL** instance, keep the Chroma `chat_memory` collection as-is, and add:

- `GET /projects/{team_id}/sessions` — list a user's chat sessions.
- `GET /projects/{team_id}/sessions/{session_id}` — full readable transcript (the data already exists in the `messages` table; we just expose it).
- Attach in-chat uploaded files to the session record; delete file + extracted data when the session is deleted.

## 9. Team scope enforcement

Every ingestion and every query carries `team_id` from the app. The microservice filters all analytics, retrieval, and predictions to that team. Because the app already authenticates the user and resolves which team(s) they may select, the microservice trusts the `team_id` passed over the authenticated channel (§4D) rather than re-implementing auth.

## 10. Open items I need from you

1. **App DB / API schema:** the table + column names (or API response shapes) for stats, and especially for any **non-PDF data** the chatbot should generalize to (injuries, roster, availability). Needed to wire §4C and the "doctor asks for most injured players" case — that data is in **no** PDF today.
2. **"Suggestion" definition:** which question types should trigger prediction-prioritized answers, and what the recommendation should look like (starting lineup / more-minutes / matchup). (§7)
3. **Auth between app and microservice:** shared token, mTLS, or VPN/private network? (§4D)
4. **Webhook payload shape:** does the app attach the PDF bytes, or send URLs the microservice pulls? Any size limits.
5. **Model persistence & retrain cost:** confirm we can persist the trained model to disk (joblib) and retrain on each upload — and whether retraining per upload is acceptable latency, or it should be batched/scheduled.
6. **Embeddings for the new PDF types:** today only stats + general chunks are embedded; confirm whether plus/minus, lineup, and PBP text should also be searchable via RAG, or only feed the model.
7. **History retention/privacy:** how long readable chat history and in-chat files are kept, and who can read another user's sessions within a team.

## 11. Suggested build phases (revised, dependency-ordered)

**Phase 0 — App-side multi-PDF capture (prerequisite, in the .NET app).** Add a `MatchStatsDocument` child table so a match can hold one PDF per type (box_score / plus_minus / lineup / play_by_play) instead of a single `RawPdfPath`. Make the upload endpoint type-aware (upsert per type), add a per-type download endpoint (the future webhook's pull-URLs), and keep the legacy `RawPdf*` fields synced for the box score. *Nothing downstream can work until the app can store the four PDFs.*

**Phase 1 — Contract + scaffolding.** Define the webhook (sport-gated, sends signed pull-URLs) and the service token/mTLS. Stand up the microservice with `project_id = TeamId` isolation and confirm the existing chatbot runs unchanged behind it.

**Phase 2 — Microservice hardening (do before wiring the model).** Replace the `MASTER_*` globals with per-team disk-persisted state (joblib + parquet); route all ingestion through a per-team serialized queue; webhook returns 202 and enqueues.

**Phase 3 — Ingestion.** Accept the pull-URLs, take the canonical box score from the app, self-extract only plus/minus + lineup + PBP, build the Chroma index per team.

**Phase 4 — Prediction wiring.** Vendor the model, retrain on match-complete inside the serialized worker, persist model + predictions to a `predictions` table.

**Phase 5 — Suggestion routing.** Add a predictions-first branch in `question_service` for `squad_recommendation` / `player_opportunity_recommendation`, falling back to the historical recipes.

**Phase 6 — Readable history + in-chat uploads.** Migrate the `messages` table (and predictions/extracted-stats) to the microservice's Postgres; keep Chroma as-is; expose session list + transcript endpoints.

**Phase 7 — App-API hybrid.** Add the live calls for non-PDF data (injuries/roster) once schema is shared.

**Phase 8 — Verification.** End-to-end: basketball upload → webhook → predictions ready → ask analytics, RAG, and suggestion questions for the right team; confirm a non-basketball upload is *not* forwarded, scope isolation holds, and history reads back.
```
