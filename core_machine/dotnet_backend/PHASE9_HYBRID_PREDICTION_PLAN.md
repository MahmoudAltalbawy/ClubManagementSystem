# Phase 9 — Hybrid DB + Prediction-First Equipo Chatbot

## North star

Equipo answers from three coordinated sources, with the **prediction model at the center of the data pipeline**, not as a side feature.

- The prediction model **trains on** plans PDFs + match stats + player stats and **emits a per-team CSV**.
- That CSV is the chatbot's **primary data source**. The chatbot reads the CSV, not the raw PDFs — unless the coach explicitly asks for a PDF answer.
- At prediction time, the model **also pulls current state from the live DB** (who is available/injured/fatigued now) so it never suggests an unavailable player.
- The **live DB** answers the non-prediction factual questions the CSV doesn't carry (injuries today, schedule, attendance, roster).
- **Plans are advise-only**: the chatbot reads a plan and suggests improvements, but never writes back.

### Routing priority (the rule that governs everything)

1. **Lineup suggestion or prediction question** → prediction model / CSV answers first. This is the default path.
2. **Question that needs no prediction** → search the PDFs and the live DB.

### Two ways the DB feeds the model (keep these separate)

- **Inference-time inputs** (Phase 9 ships this): at the moment of a prediction, pull current availability/fitness from the DB and apply it as a filter/adjustment on the model's output. No retraining. Biggest correctness win for least work.
- **Training features** (later, after history accumulates): fold DB-derived columns (games missed to injury, attendance rate, fitness trend) into the training set, taking care to avoid leakage (only use what was known before each match).

---

## What already exists (baseline from Phases 0–8)

- **.NET proxy**: `POST /chatbot/ask` (`ChatbotController.cs`), `[Authorize]`, validates question/team, scope-checks via `CanViewTeamAsync`, forwards to microservice `projects/{teamId}/ask` over the named HttpClient `ChatbotMicroservice` with the service token. Returns 503 when `Microservice:Enabled` is false.
- **Internal read endpoint (roster only)**: `GET /internal/teams/{teamId}/roster` (`InternalTeamDataController.cs`), service-token guarded, returns members with `is_injured` / `injury_type` derived from uncleared `MedicalRecords`.
- **Chatbot client**: `AppDataClient` (`app_data_client.py`) — currently **off** unless `APP_API_BASE_URL` is set.
- **Microservice**: FastAPI "final chatbot". `api/main.py` exposes `/projects/{id}/ask` (no token) and the webhook ingest (token-guarded, enqueues ingest+retrain on `TeamTaskQueue`). `question_service.ask()` does memory → load box scores → classify → predictions-first/roster-first routing.
- **Prediction code**: `prediction_service.py` → `fiba_clean_project` (`pipeline.py`, `model.py`).
- **Config**: `appsettings.json` → `Microservice` block (`Enabled`, `BaseUrl`, `ServiceToken`, `PublicBaseUrl`).

Phase 9 generalizes the roster endpoint into a family of read endpoints, turns on `AppDataClient`, adds a DB-intent router, makes the CSV primary, and wires the DB into prediction.

---

## Phase 9.0 — Contract & config foundation

**Objective:** establish the .NET ↔ chatbot internal read contract and turn the channel on, with no behavior change yet.

**Work**
- Define the internal read contract (versioned, snake_case JSON, service-token guarded). One base path: `GET /internal/teams/{teamId}/...`.
- Add config:
  - `.NET appsettings.json` → confirm `Microservice` block; add `Internal:ServiceToken` if you want a distinct token from the chatbot→.NET direction (recommended: separate secret for the reverse call).
  - Chatbot `.env` → `APP_API_BASE_URL` (the .NET base URL the chatbot calls back into) and `APP_API_SERVICE_TOKEN`.
- Confirm both directions of service-token auth: chatbot→.NET (new, this phase) and .NET→chatbot (existing).

**Acceptance:** `AppDataClient` can hit the existing `/roster` endpoint end-to-end with the new env vars set; feature stays dark behind a flag.

---

## Phase 9.1 — Generalize the internal read endpoints (.NET)

**Objective:** expose every team-scoped read the chatbot needs, all guarded and scoped exactly like the roster endpoint.

**New endpoints** (all `GET /internal/teams/{teamId}/...`, service-token guarded, team-scoped):

| Endpoint | Returns |
|---|---|
| `/roster` (exists) | members + `is_injured`, `injury_type`, `injury_count` (add count) |
| `/injuries` | active injuries: player, type, severity, since, expected return |
| `/availability` | per-player available / unavailable + reason (the inference-time feed for prediction) |
| `/schedule` | upcoming events/matches: opponent, date, location |
| `/attendance` | per-player attendance rate over a window |
| `/fitness` | latest fitness/load metrics + trend flags |
| `/player-stats` | per-player aggregates the DB holds (not the PDF-derived ones) |
| `/plans` | active/training plans for the team (read-only) |

**Work**
- Refactor `InternalTeamDataController` shared scope + auth into a base/helper so each new action stays thin.
- Each action: resolve team, enforce scope, project to the DTO, no writes.
- Add `injury_count` to the roster projection (covers the "more than one medical record" case).

**Acceptance:** each endpoint returns correct scoped data for a seeded team; a foreign team id returns empty/forbidden; unauthenticated (no token) returns 401.

---

## Phase 9.2 — Expand the chatbot's AppDataClient

**Objective:** give the chatbot typed methods to read each endpoint.

**Work**
- Add methods to `app_data_client.py`: `get_injuries`, `get_availability`, `get_schedule`, `get_attendance`, `get_fitness`, `get_player_stats`, `get_plans` (mirroring `get_roster`).
- Centralize: base URL from `APP_API_BASE_URL`, bearer from `APP_API_SERVICE_TOKEN`, short timeout, graceful failure (return `None`/empty so the chatbot degrades instead of 500-ing — mirror the existing roster behavior).
- Cache per request/turn where cheap (availability is read once per prediction).

**Acceptance:** unit-mock each method against the Phase 9.1 shapes; with `APP_API_BASE_URL` unset, all methods no-op cleanly (feature stays dark).

---

## Phase 9.3 — DB-intent router

**Objective:** classify each question into the right lane, honoring the prediction-first rule.

**Routing order**
1. **Prediction intent** (lineup, "who should start", "predict next match", projected score) → prediction lane (Phase 9.4). Highest priority.
2. **DB-fact intent** (injuries, schedule, attendance, roster, fitness, plan lookup) → live-DB lane (Phase 9.2 reads).
3. **Plan-improvement intent** ("how can this plan be better") → advisory/synthesis lane (Phase 9.5).
4. **Document intent / fallback** ("what did the match report say", or anything unmatched) → PDF/RAG lane.

**Work**
- Extend the existing classifier in `question_service.py`. Keep the current predictions-first/roster-first scaffolding; add the DB-fact and plan-improvement branches.
- Use Groq for fuzzy intent, with keyword fast-paths for the obvious cases (cheaper, deterministic).
- Make the lane explicit in the response payload (`route` / `type` already surfaced to Flutter) for debuggability.

**Acceptance:** a labeled set of ~20 sample questions routes to the expected lane; lineup/predict questions never fall through to PDF.

---

## Phase 9.4 — Prediction model: CSV primary + DB at inference

**Objective:** make the CSV the primary source and constrain predictions by current availability.

**Work**
- **CSV as primary:** the prediction pipeline writes a per-team CSV (`prediction_service` / `fiba_clean_project`). The chatbot's data load reads this CSV first; PDFs/RAG only on explicit document questions.
- **Regeneration trigger** (decision below): regenerate the CSV when new stats/plans arrive (on upload) and on backfill — not on every ask. Reads stay fast; writes happen on ingest.
- **Inference-time DB constraint:** before returning a lineup/prediction, call `AppDataClient.get_availability(team_id)` and drop/penalize unavailable players. The model ranks; the DB filters. If the DB call fails, fall back to model-only and flag it in the answer.

**Open decision (carried from chat):** regenerate CSV **on every new upload** (always fresh, more compute) vs **on demand when a prediction is first asked** (lazy, cheaper). Recommendation: **on ingest/backfill**, so the first prediction is instant and the CSV reflects full history.

**Acceptance:** a prediction excludes a player the DB marks unavailable; with the DB down, the prediction still returns (model-only) with a degraded-mode note.

---

## Phase 9.5 — Plan advisory / synthesis (advise-only)

**Objective:** answer "how can this plan be improved" by reasoning over multiple sources — read and suggest only, never write.

**Work**
- New synthesis handler: gather the active plan (`get_plans`) + recent stats (CSV) + injuries/fitness (`get_injuries`, `get_fitness`) + the model's next-match projection.
- Hand all of it to Groq with an instruction to produce concrete, grounded suggestions (e.g., "three players flagged high-fatigue and a tough projected match — reduce scrimmage load this week").
- **No write path.** The chatbot returns suggestions; the coach edits the plan manually. No write endpoints, no approval flow.

**Acceptance:** a plan-improvement question returns suggestions that reference the actual plan + at least one live signal (injury/fitness/projection); no DB mutation occurs.

---

## Phase 9.6 — Auto-backfill of old PDFs on first ask

**Objective:** old pre-microservice PDFs get indexed and folded into the model without the coach re-uploading.

**Work**
- On the .NET proxy path (`ChatbotController`), before forwarding the first question for a team, check the microservice `projects/{id}/status`. If the team was never ingested, trigger backfill: enqueue the team's existing stored PDFs into the ingest+retrain queue (`TeamTaskQueue`).
- Idempotent: a `backfilled` marker per team so it runs once.
- Backfill means **feed old PDFs into the model** (so the CSV reflects full history), not merely index them for RAG.

**Acceptance:** a team with only old PDFs, asked a prediction for the first time, triggers backfill once; subsequent asks skip it; the CSV reflects the backfilled history.

---

## Phase 9.7 — LLM phrasing layer

**Objective:** all lanes return natural-language answers, not raw rows (decision already made: LLM-phrased).

**Work**
- After each lane produces structured data, pass it through Groq to phrase the answer in the coach's terms.
- Keep a deterministic fallback template per lane in case Groq is unavailable, so the chatbot still answers.
- Preserve `session_id` threading across turns (already wired in Flutter `EquipoService` + `AskEquipoView`).

**Acceptance:** each lane's answer reads naturally; with Groq disabled, a templated answer still returns.

---

## Phase 9.8 — Verification

**Objective:** prove the whole hybrid behaves, end to end.

**Work**
- Integration tests per lane against a seeded team (prediction, DB-fact, plan-advisory, document).
- Scope tests: foreign team id leaks nothing on every new endpoint.
- Degraded-mode tests: DB down → predictions still return; Groq down → templates return; microservice off → proxy 503 with the friendly message.
- Manual pass through the Flutter Ask Equipo page covering one question per lane.
- (High-stakes) consider a verification subagent to re-check the scope guards on all new internal endpoints.

**Acceptance:** all lanes green; no cross-team leakage; graceful degradation confirmed in all three failure modes.

---

## Sequencing & dependencies

```
9.0 contract/config
   └─> 9.1 .NET read endpoints ──> 9.2 AppDataClient methods
                                      ├─> 9.3 intent router
                                      ├─> 9.4 CSV primary + DB-at-inference
                                      ├─> 9.5 plan advisory
                                      └─> 9.7 LLM phrasing
   9.6 auto-backfill (needs 9.0 + microservice status)  ──> feeds 9.4
   9.8 verification (last, depends on all)
```

Ship order recommendation: 9.0 → 9.1 → 9.2 → 9.4 (the highest-value correctness win) → 9.3 → 9.6 → 9.5 → 9.7 → 9.8.

## Open decisions to confirm before build

1. **CSV regeneration trigger** — on ingest/backfill (recommended) vs on-demand at first prediction.
2. **Reverse service token** — separate secret for chatbot→.NET, or reuse the existing one.
3. **Player-stats source of truth** — when a stat exists both in the CSV (PDF-derived) and the DB, which wins for a plain factual question?
