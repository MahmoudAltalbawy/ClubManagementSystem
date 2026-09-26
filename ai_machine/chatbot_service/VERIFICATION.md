# Integration Verification Runbook (Phases 0–8)

This integration was implemented across the .NET app and the Python microservice. The
build/run sandbox was unavailable during implementation, so **nothing here has been
compiled or executed yet** — this runbook is the verification checklist to run once a
real environment is available, plus an honest list of the riskiest unverified spots.

## 1. Build the .NET solution

```
dotnet build SportsPlatform.Auth.sln
```

Files added/changed on the app side:
- `Core/Interfaces/IChatbotWebhookDispatcher.cs` (+ payload DTOs, snake_case wire)
- `Infrastructure/Services/ChatbotWebhookDispatcher.cs` (feature-flagged, swallows failures)
- `Infrastructure/SportsPlatform.Auth.Infrastructure.csproj` (added `Microsoft.Extensions.Http`)
- `Api/Controllers/InternalMatchStatsController.cs` (service-token PDF pull-back)
- `Api/Controllers/InternalTeamDataController.cs` (service-token roster/injury pull — Phase 7)
- `Api/Controllers/MatchStatsPdfController.cs` (dispatch webhook on basketball uploads)
- `Api/Program.cs` (named HttpClient + dispatcher DI)
- `Api/appsettings.json` (`Microservice` block)
- Phase 0: `MatchStatsDocument` entity/config/DbSet, migration `048`, type-aware upload/download

Watch for: the two internal controllers are auto-discovered (no manual registration). They
rely on `AppDbContext` DbSets `Teams`, `TeamMemberships`, `PlayerProfiles`, `MedicalRecords`
and enums `MembershipStatus`, `RoleNameType` — all already used by `TeamService`.

## 2. Microservice smoke test

```
cd "scripts/final chatbot"
pip install -r requirements.txt          # ensure fastapi, requests, pandas, chromadb, etc.
python -m py_compile $(git ls-files '*.py')   # static check first
export MICROSERVICE_SERVICE_TOKEN=<same token as appsettings Microservice:ServiceToken>
uvicorn api.main:app --port 8200
```

Then:
- `GET /health` → 200, `groq_configured` reflects env.
- `POST /webhooks/match-stats-updated` **without** the bearer token → 401.
- With token + a valid payload → 202 `{"status":"queued"}`; the per-team worker then
  pulls PDFs, rebuilds the RAG index, and retrains the model in the background.

## 3. End-to-end (the Phase 8 acceptance path)

1. Upload a **basketball** match PDF in the app → confirm the app POSTs the webhook
   (set `Microservice:Enabled=true`). A **non-basketball** upload must NOT forward
   (gated by `IsBasketballMatch`).
2. Poll `GET /projects/{team_id}/status` until box-score CSV + chunks + Chroma exist.
3. Ask questions for that `team_id` (== `project_id`):
   - analytics: "top scorers" → ranked from box score.
   - RAG: "summarize the game" → retrieved from chunks.
   - **prediction** (Phase 5): "who will perform best next match" → served from
     `test_predictions.csv` (model projections), not the box score.
   - **roster/injury** (Phase 7): "who is injured" → served from the app roster IF
     `APP_API_BASE_URL` is set; otherwise falls through to RAG.
4. Readable history (Phase 6): `GET /projects/{id}/sessions`,
   `GET /projects/{id}/sessions/{session_id}`, `DELETE` to clear, and in-chat upload
   `POST /projects/{id}/sessions/{session_id}/uploads`.
5. Scope isolation: a second `team_id` must not see the first team's PDFs/history/model.

## 4. Environment flags

| Variable | Purpose | Default |
|---|---|---|
| `MICROSERVICE_SERVICE_TOKEN` | Shared service-to-service bearer (both sides) | unset → service endpoints fail closed |
| `FIBA_PROJECT_DIR` | Path to `fiba_clean_project` for the model | `../fiba_clean_project` |
| `CHAT_HISTORY_DSN` | If set, chat history uses Postgres (shared, project-scoped) | unset → per-project SQLite |
| `APP_API_BASE_URL` | App base URL for live roster/injury pulls | unset → roster questions fall through to RAG |
| `APP_API_TIMEOUT` | Roster pull timeout (s) | 8 |

## 5. Unverified / highest-risk spots to check first

1. **`CHAT_HISTORY_DSN` Postgres backend** — written but never run. Needs `psycopg` (v3)
   installed and a reachable DB. Default (SQLite) path is unchanged and is the safe default;
   only enable Postgres after testing. The legacy-SQLite `project_id` column is added via
   `ALTER TABLE` on init — verify against an existing `chat_history.db`.
2. **Internal roster endpoint authorization** — `InternalTeamDataController` intentionally
   skips the per-user `EnsureCanViewTeam` check that `TeamService.GetTeamMembersAsync` applies,
   because the shared service token is the authorization (same pattern as the PDF pull
   controller). Confirm this is acceptable for your threat model and that the endpoint is only
   reachable on the trusted internal network.
3. **Filename classification** — pulled PDFs are renamed to the canonical phrases
   ("FIBA Box Score", "Player PlusMinus Summary", "Line Up Analysis", "Play by Play") so the
   extractor classifies them. Verify a real pulled file is not parsed as "Unknown".
4. **Prediction column names** — formatter reads `Name` + `predicted_EF` from
   `test_predictions.csv` (confirmed against `pipeline.py`); re-confirm if the model output
   schema changes.
5. **pydantic version** — webhook schemas use plain snake_case fields (no aliases), and the
   C# DTOs serialize snake_case, so the wire format is version-agnostic. Confirm on the
   installed pydantic version anyway.
