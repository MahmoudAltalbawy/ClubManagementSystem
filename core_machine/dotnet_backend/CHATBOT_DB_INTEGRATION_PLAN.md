# Chatbot ↔ DB Integration Plan — fixing broad/irrelevant answers

## 1. Why specific questions get broad answers (root cause)

A coach asks something specific ("best 3-point shooters", "how did we do vs X", "top
scorer last game"). What happens today:

1. The question hits the hybrid lanes — but every existing lane is **soccer/roster/
   schedule** shaped. None of them understands a basketball stat query.
2. It falls through to `classify_question`, which looks at the **box-score CSV**
   (`players_box_scores.csv`). That CSV is **empty** (the Python PDF extractor yields 0
   player rows for these FIBA PDFs), so `available_columns` is empty and the planner
   returns `rag` / `unsupported`.
3. RAG then retrieves loosely-matching PDF chunks and the LLM produces a **vague,
   "broad" answer** — or says it "didn't find the document".

Meanwhile the **actual structured basketball data already lives in the database** and is
never read by the chatbot:

- `PlayerMatchStats` — per-player box scores: `BbPoints`, `ThreePtMA`, `TwoPtMA`,
  `FtMA`, rebounds, `BbSteals`, `BbBlocks`, `BbAssists`, `BbTurnovers`, `BbEfficiency`,
  `IsStarter`, `IsCaptain`, `GamesPlayed`.
- `MatchStats` — team box scores: team points, `ThreePtMA`, result, opponent, venue.
- `MatchAnalysisReport` — per-game summary, final score, result, opponent, competition.
- `MatchLineupAnalysis` — 5-man lineup +/-, on-court time, points for/against.

So there are **two distinct defects**, and both must be fixed:

- **(A) Exposure gap** — the basketball/coaching tables aren't reachable by the chatbot.
- **(B) Specificity gap** — even with data, there's no lane that parses *which player* and
  *which metric* the coach asked about and answers *only that*. The bot answers broadly
  because nothing pins it to the specific entity.

## 2. Tables to wire up (per your selection)

| Table | DbSet | New internal endpoint |
|---|---|---|
| PlayerMatchStats | `_db.PlayerMatchStats` | `GET /internal/teams/{id}/match-player-stats` |
| MatchStats | `_db.MatchStats` | `GET /internal/teams/{id}/match-team-stats` |
| MatchAnalysisReport | `_db.MatchAnalysisReports` | `GET /internal/teams/{id}/match-reports` |
| MatchLineupAnalysis | `_db.MatchLineupAnalyses` | `GET /internal/teams/{id}/lineup-analysis` |
| CoachingLineup + Players | `_db.CoachingLineups` / `CoachingLineupPlayers` | `GET /internal/teams/{id}/coaching-lineups` |
| CoachNote | `_db.CoachNotes` | `GET /internal/teams/{id}/coach-notes` |
| Season | `_db.Seasons` | `GET /internal/teams/{id}/seasons` |

All read-only, all token-guarded (`IsServiceTokenValid`), all snake_case DTOs — same
pattern as the existing `InternalTeamDataController` endpoints.

## 3. Phase A — .NET: expose the tables

Add to `InternalTeamDataController.cs`. For each, a `GET` + a snake_case DTO.

### A1. Per-player basketball box scores (the 3-point fix)
`GET /internal/teams/{teamId}/match-player-stats`

- Query `_db.PlayerMatchStats.Where(s => s.TeamId == teamId)`.
- Default to per-game player rows: `RowType == "player"` and `Status == "PLAYED"`
  (exclude `team_total` / `DNP` / `CUMULATIVE` so aggregates are clean).
- Join to `User.Name` via `PlayerUserId`. Join `Event`/`MatchStats` for date + opponent.
- DTO `InternalMatchPlayerStatDto` fields (snake_case, sent **raw** — the Python client
  passes dicts through verbatim so no client change is needed to add a field):
  `user_id, name, event_id, match_date, opponent, player_no, is_starter, is_captain,
  bb_points, two_pt_ma, three_pt_ma, ft_ma, offensive_rebounds, defensive_rebounds,
  total_rebounds, bb_assists, bb_turnovers, bb_steals, bb_blocks, bb_personal_fouls,
  bb_efficiency, bb_minutes, games_played`.
- Return all rows; the chatbot aggregates/ranks (keeps the endpoint dumb and reusable).

### A2. Team box scores
`GET /internal/teams/{teamId}/match-team-stats` → `InternalMatchTeamStatDto`:
`match_stats_id, event_id, match_date, opponent_name, team_score, opponent_score,
result, venue, competition_name, three_pt_ma, two_pt_ma, ft_ma, total_rebounds,
bb_assists, turnovers, steals, blocks, points, category, matchup`.

### A3. Match analysis reports
`GET /internal/teams/{teamId}/match-reports` → `InternalMatchReportDto`:
`report_id, opponent_code, opponent_name, match_date, competition, venue, game_no,
team_score, opponent_score, result, summary`. Order by `MatchDate` desc.

### A4. Lineup +/- analysis
`GET /internal/teams/{teamId}/lineup-analysis` (join through `MatchAnalysisReport` to
scope by team) → `InternalLineupAnalysisDto`:
`report_id, opponent_name, match_date, lineup_players, time_on_court, time_seconds,
points_for, points_against, score_diff, points_per_minute, rebounds, steals,
turnovers, assists`.

### A5. Coaching lineups (tactics)
`GET /internal/teams/{teamId}/coaching-lineups` → `InternalCoachingLineupDto`:
`lineup_id, title, formation, game_model, tactical_notes, event_id, created_at` +
nested `players` (from `CoachingLineupPlayer` — include player name + position/role).
Filter `DeletedAt == null`.

### A6. Coach notes
`GET /internal/teams/{teamId}/coach-notes` → `InternalCoachNoteDto`:
`note_id, event_id, author_role, body, created_at`. Filter `DeletedAt == null`,
order desc, cap ~50.

### A7. Seasons
`GET /internal/teams/{teamId}/seasons` → `InternalSeasonDto`:
`season_id, label, start_date, end_date, is_current`.

### A8. Fix field gaps on ALREADY-exposed endpoints
These endpoints exist but drop columns the coach can legitimately ask about:

- **Fitness** (`InternalFitnessDto`): currently only `bmi, body_fat_pct,
  speed_test_result, endurance_score, test_date`. **Add**: `height`, `weight`,
  `custom_test_name`, `custom_test_result`, and `recorded_by_name` (resolve
  `FitnessUserId`/`CreatedBy` → user name). Note: `FitnessRecord` carries its own
  time-series `Height`/`Weight` distinct from the canonical `PlayerProfile.Height/
  Weight` used by the roster lane — answer "how tall is X" from the profile, "what was
  his weight at the last fitness test" from the fitness record, and never conflate them.
- **Roster** (`InternalRosterMemberDto`): consider `email`/`dob`/`years_of_experience`
  only if you want profile questions (currently excluded — you chose "no bio fields").

### A9. Cross-cutting: expose "who recorded/added it" (the recorder gap)
Almost every data-bearing table stores the staff member who entered the row, but no
endpoint resolves that id to a name, so the bot can't answer "who logged this injury /
recorded these stats / wrote this note". Add a resolved `*_by_name` field to each:

| Table → endpoint | Id column(s) | New field |
|---|---|---|
| MedicalRecord → injuries | `DoctorUserId`, `CreatedBy` | `recorded_by_name` |
| FitnessRecord → fitness | `FitnessUserId`, `CreatedBy` | `recorded_by_name` |
| PlayerGameStats → player-stats | `RecordedBy` | `recorded_by_name` |
| MatchStats → match-team-stats | `RecordedBy` | `recorded_by_name` |
| Attendance → attendance | `RecordedByUserId` | `recorded_by_name` |
| CoachNote → coach-notes | `AuthorUserId` (+`AuthorRole`) | `author_name` |
| CoachingPlan/Lineup | `CreatedBy` | `created_by_name` |

Implement with one shared `Dictionary<Guid,string>` name lookup (one `Users` query per
request) reused across the controller, rather than per-row joins.

## 4. Phase B — Python `AppDataClient`

Add one getter per endpoint, mirroring the existing `_list(self._get(...), key)` style.
They return raw dicts so new fields flow through automatically:

```
get_match_player_stats(team_id)   -> _list(_get(team_id, "match-player-stats"), "players")
get_match_team_stats(team_id)     -> _list(_get(team_id, "match-team-stats"), "matches")
get_match_reports(team_id)        -> _list(_get(team_id, "match-reports"), "reports")
get_lineup_analysis(team_id)      -> _list(_get(team_id, "lineup-analysis"), "lineups")
get_coaching_lineups(team_id)     -> _list(_get(team_id, "coaching-lineups"), "lineups")
get_coach_notes(team_id)          -> _list(_get(team_id, "coach-notes"), "notes")
get_seasons(team_id)              -> _list(_get(team_id, "seasons"), "seasons")
```

## 5. Phase C — Python routing + the specificity layer (fixes the "broad answer" half)

This is the core fix for "needs specific questions, gives a broad answer."

### C1. A shared entity/metric extractor
Add `_extract_query_focus(question, roster)` that returns `(player_name|None,
metric|None, scope)`:

- **Player**: fuzzy-match tokens in the question against live roster names
  (`get_roster`). If exactly one matches → that player. If several → mark ambiguous.
- **Metric**: keyword → canonical stat:
  `3 point|three|3pt|beyond the arc → three_pt`, `points|scoring|scorer → points`,
  `rebound|board → rebounds`, `assist → assists`, `steal → steals`, `block → blocks`,
  `turnover → turnovers`, `efficiency|impact → efficiency`, `free throw|ft → ft`.
- **Scope**: `last game | this season | vs <opp> | overall`.

### C2. A basketball-stats lane (before the soccer analytics/RAG fallthrough)
`_looks_like_player_stats` + `_answer_player_stats(project_id, question)`:

1. Pull `get_match_player_stats`. If `None`/empty → return `None` (fall through).
2. Parse `ThreePtMA`/`TwoPtMA`/`FtMA` `"made/attempted"` strings into ints with a small
   `_parse_made_attempted` helper; aggregate per player (sum makes, attempts, points,
   rebounds, etc.) across the chosen scope.
3. Branch on the extracted focus:
   - **Specific player + metric** → one-line precise answer
     ("Ahmed has 14 made threes on 39 attempts (35.9%) across 6 games").
   - **Metric, no player** ("best 3-point shooters") → ranked top-N on that metric,
     with a sensible minimum-attempts filter so small samples don't top the list.
   - **Player, no metric** ("how is Ahmed doing") → that player's stat line only.
   - **Ambiguous player** (matched >1) → **ask a clarifying question** instead of
     dumping everyone. This is the key anti-"broad answer" behavior.
4. Pass the deterministic draft through `_phrase_db_answer` (facts computed in Python;
   Groq only rewords).

### C3. Team-result / report lane
`_looks_like_match_result` (`how did we do`, `last game`, `result`, `score`, `vs`,
`record`, `win|loss`) → `_answer_match_results` using `get_match_reports` +
`get_match_team_stats`. Filter by opponent when the question names one.

### C4. Lineup lanes
- `_looks_like_lineup_analysis` (`best lineup`, `plus minus`, `+/-`, `on court`,
  `which five`) → `_answer_lineup_analysis` (`get_lineup_analysis`, rank by `score_diff`
  or `points_per_minute`).
- `_looks_like_coaching_lineup` (`formation`, `our lineup`, `tactics`, `game model`) →
  `_answer_coaching_lineups` (`get_coaching_lineups`).

### C5. Coach-notes lane
`_looks_like_coach_notes` (`coach note`, `notes from`, `what did the coach say`) →
`_answer_coach_notes`.

### C6. Lane ordering (in `ask()`)
Insert the basketball stat/result lanes **before** the soccer analytics + RAG
fallthrough, and **after** prediction/plan-advice (so model-first and advisory intent
still win). Proposed order:
`plan_advice → schedule → prediction → injuries → match_result → player_stats →
lineup_analysis → coaching_lineup → coach_notes → roster → profile → attendance →
fitness → plans → (classifier: analytics/rag)`.

### C7. Make "unsupported" stop being broad
When the classifier would return `rag`/`unsupported` for a clearly stat-shaped question
(metric detected) but no DB data exists, return a **specific** "I don't have box-score
data loaded for this team yet" rather than a vague RAG paragraph.

## 6. Phase D — answer style

For every new lane: short, factual, entity-anchored. Never list the whole team when the
coach named one player. When the question is ambiguous (no player and no metric, or a
name that matches several), ask **one** clarifying question. This directly converts
"broad" responses into precise ones.

## 7. Phase E — verification

- `dotnet build` the API (sandbox is currently down — must be run on your machine).
- `py_compile` the chatbot package.
- Manual matrix against a real team id: "best 3-point shooters", "top scorer",
  "how did we do vs <opp>", "best lineup", "our formation", "Ahmed's stats",
  plus an ambiguous "how's our guard doing" to confirm it asks for clarification.
- Confirm graceful degradation: with `APP_API_BASE_URL` unset, every new lane no-ops
  and the old behavior is unchanged.

## 8. Open dependency

The DB tables only help if they're **populated**. `PlayerMatchStats`/`MatchStats` are
filled by the .NET app's PDF box-score importer when a coach uploads a box-score PDF
to a match/event. Confirm that importer has run for the test team — otherwise the lanes
will (correctly) report "no box-score data yet", and we'd be looking at an *ingestion*
gap rather than a chatbot gap. (This also makes the old, broken Python CSV extractor
redundant — once the DB lane works, the chatbot should prefer the DB over the CSV.)

## 9. Complete field-by-field audit (all 41 tables)

The database has **41 tables** (`AppDbContext` DbSets). Legend: ✓ exposed to chatbot
today · ➕ approved to add (your selection) · ✗ not exposed · 🔒 skip (auth/infra/PII).

### Group 1 — Exposed today (with per-column gaps flagged)

**TeamMembership** → roster. `TeamMembershipId`✗ `TeamId`✓ `UserId`✓(user_id)
`Role`✓ `Status`✓(filter) `InvitedBy`✗ `JoinedAt`✗ `CreatedAt/UpdatedAt`✗.

**PlayerProfile** → roster. `PlayerId`✗ `UserId`✓ `Position`✓ `JerseyNumber`✓
`Height`✓ `Weight`✓ `DeletedAt`✓(filter) timestamps✗.

**User** → (name only). `UserId`✓ `Name`✓ `Email`✗ `Username`✗ `PhoneNumber`🔒
`Dob`✗ `Bio`✗ `YearsOfExperience`✗ `ProfileImageUrl`✗ `IsAdmin`🔒. *(You chose to
exclude bio fields — left ✗.)*

**MedicalRecord** → injuries. `RecordId`✗ `TeamId`✓ `PlayerId`✓ `DoctorUserId`**✗→➕
recorded_by_name** `RecordDate`✓ `InjuryType`✓ `Diagnosis`✓ `ExpectedReturnDate`✓
`RecoveryTips`✓ `IsCleared`✓(filter) `CreatedBy`✗ `UpdatedBy`✗.

**FitnessRecord** → fitness. `FitnessId`✗ `TeamId`✓ `PlayerId`✓ `FitnessUserId`**✗→➕
recorded_by_name** `TestDate`✓ `Height`**✗→➕** `Weight`**✗→➕** `Bmi`✓ `BodyFatPct`✓
`SpeedTestResult`✓ `EnduranceScore`✓ `CustomTestName`**✗→➕** `CustomTestResult`**✗→➕**
`CreatedBy/UpdatedBy`✗. *(This is the table you flagged.)*

**Attendance** → attendance. `AttendanceId`✗ `EventId`✓ `InstanceDate`✓(window)
`PlayerId`✓ `RecordedByUserId`**✗→➕ recorded_by_name** `Status`✓ `Notes`✗ timestamps✗.

**Event** → schedule. `EventId`✓ `TeamId`✓ `SeasonId`✗ `CreatedBy`✗ `Title`✓
`Description`✗(could add) `Location`✓ lat/long✗ `StartAt`✓ `EndAt`✓ `EventType`✓
`Timezone`✗ `RecurrenceRule`✗ `RecurrenceEndDate`✗ `DeletedAt`✓(filter).

**PlayerGameStats** (soccer) → player-stats. `StatId`✗ `TeamId`✓ `PlayerUserId`✓
`EventId`✗ `RecordedBy`**✗→➕ recorded_by_name** `MatchDate`✗(could add) `OpponentName`✗
`MinutesPlayed`✓ `Goals`✓ `Assists`✓ `YellowCards`✓ `RedCards`✓ `Rating`✓ `Notes`✗.

**CoachingPlan** → plans. `PlanId`✓ `TeamId`✓ `CreatedBy`**✗→➕ created_by_name**
`Title`✓ `Description`✓ `Content`✓ `Visibility`✓ `DeletedAt`✓(filter).

### Group 2 — Approved to add (every column)

**PlayerMatchStats** (basketball, the 3-pt fix). Soccer dup cols: MinutesPlayed/Goals/
Assists/ShotsOnTarget/TotalShots/Passes*/Tackles/Interceptions/Yellow/Red/Rating/Notes
(likely null for basketball). Basketball: `Granularity` `RowType`(filter "player")
`Status`(filter "PLAYED") `PlayerNo` `IsStarter` `IsCaptain` `GamesListed` `GamesPlayed`
`Starts` `BbMinutes` `TwoPtMA` `ThreePtMA` `FtMA` `OffensiveRebounds`
`DefensiveRebounds` `TotalRebounds` `BbAssists` `BbTurnovers` `BbSteals` `BbBlocks`
`BbPersonalFouls` `BbFoulsDrawn` `BbEfficiency` `BbPoints` + team-reb cols → all ➕.
Keys: `MatchStatsId` `TeamId`✓ `EventId` `SeasonId` `PlayerUserId`→name.

**MatchStats** (team box score). `OpponentName` `TeamScore` `OpponentScore` `Result`
`Venue` `CompetitionName` `PossessionPercent` + basketball `Category` `Granularity`
`GameNo` `Matchup` `TwoPtMA` `ThreePtMA` `FtMA` rebounds `BbAssists` `Turnovers`
`Steals` `Blocks` `PersonalFouls` `Efficiency` `Points` `Minutes` → ➕. `RecordedBy`→
recorded_by_name ➕. **Note:** `ExtractedText`/`RawPdfPath`/`RawPdf*` columns hold the
raw PDF text — RAG territory, leave to the existing PDF pipeline, don't ship in the DTO.

**MatchAnalysisReport**. `ReportId` `TeamCode` `OpponentCode` `OpponentName` `MatchDate`
`Competition` `Venue` `GameNo` `TeamScore` `OpponentScore` `Result` `Summary` → ➕.

**MatchLineupAnalysis**. `LineupPlayers` `TimeOnCourt` `TimeSeconds` `PointsFor`
`PointsAgainst` `ScoreDiff` `PointsPerMinute` `Rebounds` `Steals` `Turnovers`
`Assists` → ➕ (scope via parent report's TeamId).

**CoachingLineup**. `Title` `Formation` `GameModel` `TacticalNotes` `EventId`
`SeasonId` `Visibility` `CreatedBy`→created_by_name `DeletedAt`(filter) → ➕.
**CoachingLineupPlayer** (nested). `PlayerUserId`→name `Position` `Unit` `SortOrder`
`Instructions` → ➕.

**CoachNote**. `EventId` `TeamId` `AuthorUserId`→author_name `AuthorRole` `Body`
`DeletedAt`(filter) → ➕.

**Season**. `Label` `StartDate` `EndDate` `IsCurrent` → ➕.

### Group 3 — Skip (with reason)

🔒 **UserAuthProvider** (PasswordHash, provider ids), **RefreshToken** (tokens),
**PasswordResetCode** (OTP/token hashes) — secrets, never expose.
🔒 **Conversation / ConversationParticipant / Message / MessageReaction /
MessageReadReceipt** — private DMs incl. media + GPS location; you chose to exclude.
🔒 **AppNotification** — per-user notification feed (RecipientUserId, MetadataJson).
➖ **Club / ClubMembership / Invitation / PlayerTeam** — org/membership plumbing
(Invitation also holds emails + tokens). PlayerTeam is a legacy join superseded by
TeamMembership+PlayerProfile.
➖ **EventException** — recurrence overrides; could *refine* schedule accuracy later
(cancellations/reschedules) but not a Q&A source on its own.
➖ **MedicalDocumentRequest, EventDocument, EventPlan, CoachingPlanDocument,
MatchAnalysisDocument, MatchStatsDocument, GameVideo, PlayerVideo** — file/document
pointers (storage paths, filenames). The *extracted text* already flows through RAG;
the rows themselves are just blob metadata, not chatbot answers.

### Tables you may want to reconsider
- **EventException** — if coaches ask "is Saturday's session still on?", cancellations
  live here, not in `Event`. Currently the schedule lane would show a stale time.
- **PlayerGameStats.MatchDate/OpponentName/EventId** — cheap to add; lets the soccer
  stat lane answer "vs <opp>" and per-date questions.
Tell me if either should move into scope.
