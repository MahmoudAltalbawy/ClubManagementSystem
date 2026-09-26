# Stats Section Enhancement — Implementation Plan

## Overview

Add a **"+"** button (visible only to team analysts) in the Teams → Stats section. The analyst can enter stats via two tabs (**Game** / **Training**), each with a **Per Game ↔ Cumulative** toggle. Stats can be entered manually or by uploading FIBA box-score PDFs that get extracted to structured data via `extract.py`.

Charts are rendered natively in Flutter using `fl_chart` (already a dependency), replicating the logic from `app.py`.

---

## Phase 1 — Flutter UI (Stats Entry)

### 1.1 Add the "+" FAB (TeamStatsView.dart)

- Show a `FloatingActionButton` only when `userRole == 'analyst'` (or equivalent role string from `TeamState.userRoleInSelectedTeam`).
- On tap → navigate to a new `AddStatsView`.

**File:** `lib/teamstats/TeamStatsView.dart`
**Change:** Wrap the existing `SingleChildScrollView` in a `Scaffold` body with a conditional FAB.

### 1.2 Create `AddStatsView` with Tabs + Toggle

**New file:** `lib/teamstats/AddStatsView.dart`

Structure:
```
AddStatsView
├── TabBar: [Game Stats] [Training Stats]
├── Toggle: Per Game ↔ Cumulative
├── Body (per tab):
│   ├── Manual Entry Form
│   │   ├── Opponent / Session name
│   │   ├── Date picker
│   │   ├── Stat fields (pts, reb, ast, stl, blk, to, pf, etc.)
│   │   └── Save button → StatsEntryBloc.SubmitManualStats
│   └── Upload PDF Card
│       ├── Tap → file_picker (PDF only)
│       └── Upload → StatsEntryBloc.UploadPdf
└── Result feedback (success/error snackbar)
```

**Stat fields** match `extract.py` OUTPUT_COLUMNS:
- Shooting: `2p_ma`, `3p_ma`, `ft_ma` (made/attempted format)
- Rebounds: `or`, `dr`, `reb`
- Playmaking: `ast`, `to`, `stl`, `blk`
- Discipline: `pf`, `fd`
- Summary: `eff`, `pts`, `min`

For **Training stats**, the form is simpler (no opponent/matchup, session-based metrics).

The **Per Game / Cumulative** toggle controls:
- **Per Game:** Enter stats for a single game/session.
- **Cumulative:** View/edit aggregated stats (read-mostly; the backend computes cumulative from individual entries, same logic as `add_cumulative_rows` in `extract.py`).

### 1.3 Reuse existing `UploadPdfView` pattern

The project already has `lib/members/UploadPdfView.dart` using `file_picker`. Follow the same pattern:
- `FilePicker.platform.pickFiles(type: FileType.custom, allowedExtensions: ['pdf'])`
- Show upload progress indicator
- On success, display extracted stats preview before saving

---

## Phase 2 — BLoC Layer

### 2.1 New `StatsEntryBloc`

**New file:** `lib/teamstats/stats_entry_bloc.dart`

Events:
```dart
SetStatsCategory(String category)     // "game" | "training"
SetStatsMode(String mode)             // "per_game" | "cumulative"
SubmitManualStats(Map<String, dynamic> stats)
UploadPdf(String filePath, String fileName)
PreviewExtractedData(List<Map<String, dynamic>> rows)
ConfirmExtractedData()
```

State:
```dart
StatsEntryState {
  String category;           // "game" | "training"
  String mode;               // "per_game" | "cumulative"
  bool isSubmitting;
  bool isUploading;
  List<Map<String, dynamic>>? extractedPreview;  // from PDF
  String? error;
  String? successMessage;
}
```

### 2.2 Update existing `StatsBloc`

Add a `RefreshStats` event so after a new entry is submitted, the charts and tables auto-refresh.

---

## Phase 3 — Service Layer

### 3.1 Extend `StatsService`

**File:** `lib/services/stats_service.dart`

Existing methods are sufficient for reads. Add/confirm:

```dart
// Already exists:
Future<dynamic> createStats(clubId, teamId, body)
Future<dynamic> uploadStatsFile(clubId, teamId, eventId, filePath, fileName)

// Add:
Future<Map<String, dynamic>> uploadAndExtractPdf(
    String clubId, String teamId, String filePath, String fileName) {
  return _api.uploadFile(
    '/clubs/$clubId/teams/$teamId/stats/extract',
    fileField: 'file',
    filePath: filePath,
    fileName: fileName,
  );
}

Future<dynamic> createTrainingStats(
    String clubId, String teamId, Map<String, dynamic> body) {
  return _api.post('/clubs/$clubId/teams/$teamId/stats/training', body: body);
}
```

---

## Phase 4 — Backend (PDF Extraction Pipeline)

Two options — implement whichever fits the deployment model:

### Option A: .NET API Endpoint (integrated)

Add a controller action in `SportsPlatform.Auth.Api`:

```
POST /api/clubs/{clubId}/teams/{teamId}/stats/extract
Content-Type: multipart/form-data
Body: file (PDF)
Response: { rows: [...], summary: {...} }
```

Implementation:
1. Receive the PDF, save to temp directory.
2. Shell out to Python: `python extract.py --pdf_dir <tempDir> --output_csv <tempCsv>`
3. Read the CSV, parse rows into JSON.
4. Return extracted rows to Flutter for preview.
5. On confirmation (separate POST), persist rows to DB.

**Requires:** Python + `pdfplumber` + `pandas` installed on the server, or bundled as a subprocess.

### Option B: Separate FastAPI Microservice

```python
# stats_extractor_api.py
from fastapi import FastAPI, UploadFile
from extract import extract_pdf
import tempfile, json

app = FastAPI()

@app.post("/extract")
async def extract(file: UploadFile):
    with tempfile.NamedTemporaryFile(suffix=".pdf", delete=False) as tmp:
        tmp.write(await file.read())
        tmp_path = Path(tmp.name)
    rows = extract_pdf(tmp_path)
    return {"rows": rows, "count": len(rows)}
```

Deploy alongside the main API. The .NET API proxies or the Flutter app calls it directly.

### DB Schema (both options)

New tables/entities:

```sql
TeamStats (
    Id, ClubId, TeamId, EventId?,
    Category,          -- 'game' | 'training'
    Granularity,       -- 'game_player' | 'game_team_total' | 'cumulative_player' | ...
    RowType,           -- 'player' | 'team_total'
    SourceFile,
    GameNo, GameDate, StartTime, Matchup,
    PlayerNo, PlayerName, Status,
    IsStarter, IsCaptain,
    GamesListed, GamesPlayed, Starts,
    Minutes,
    TwoPtMA, ThreePtMA, FtMA,
    OffReb, DefReb, TotalReb,
    Assists, Turnovers, Steals, Blocks,
    PersonalFouls, FoulsDrawn, Efficiency, Points,
    TeamOffReb, TeamDefReb, TeamReb, TeamPF, TeamFD,
    CreatedAt, CreatedBy
)
```

Columns map directly to `extract.py` `OUTPUT_COLUMNS`.

---

## Phase 5 — Charts & Visualization (fl_chart)

### 5.1 Port `app.py` chart logic to Flutter

The existing `TeamStatsView` already has a `LineChart` using `fl_chart`. Enhance it to support the data shape from `extract.py`:

**Attribute groups** (from `app.py`):
- **Basic:** pts, reb, ast, stl, blk, 2pts_total, 3pts_total, ft_made
- **Rebounds:** or, dr, reb
- **Discipline:** to, pf, fd
- **Impact:** eff, pts

**Chart types to implement:**
1. **Line chart** (already exists) — trend per game for selected attributes.
2. **Bar chart** — comparison across players for a single stat.
3. **Radar chart** — player profile overview (multiple stats on one chart).

### 5.2 Chart data flow

```
GET /stats → StatsBloc → StatsState.chartLines → LineChart widget
             ↓
             Also feeds attribute selector dropdown
             (matching app.py's ATTRIBUTE_GROUPS)
```

### 5.3 New widget: `StatsChartSelector`

A dropdown + multi-select for choosing which attributes to plot, mirroring `app.py`'s sidebar controls:
- Visualization level: Player / Team
- Value type: Per Game / Cumulative
- Attribute group: Basic / Rebounds / Discipline / Impact
- Attribute multi-select within group

---

## Phase 6 — File Structure Summary

```
lib/teamstats/
├── TeamStatsView.dart          (modify — add FAB)
├── AddStatsView.dart           (new — tabs + toggle + forms)
├── StatsChartSelector.dart     (new — attribute picker)
├── stats_bloc.dart             (modify — add RefreshStats)
├── stats_entry_bloc.dart       (new — entry + upload logic)

lib/services/
├── stats_service.dart          (modify — add extract + training endpoints)
```

---

## Implementation Order

| Step | Task | Depends On |
|------|------|------------|
| 1 | DB schema + backend endpoints (manual entry + PDF extract) | — |
| 2 | `stats_entry_bloc.dart` + `StatsService` extensions | Step 1 |
| 3 | `AddStatsView.dart` (tabs, toggle, manual form) | Step 2 |
| 4 | PDF upload + extraction preview in `AddStatsView` | Step 2 |
| 5 | FAB in `TeamStatsView` (role-gated) | Step 3 |
| 6 | Enhanced charts (attribute groups, player/team selector) | Step 1 |
| 7 | Training stats tab (simpler form variant) | Step 3 |
| 8 | End-to-end testing with sample PDFs | All |
