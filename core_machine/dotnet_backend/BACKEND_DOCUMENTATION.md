# SportsPlatform.Auth Backend Documentation

Generated from the source code in this repository.

This document describes the backend architecture, technologies, request flow, database model, services, security behavior, and every currently registered backend route.

## Executive Summary

The backend is an ASP.NET Core Web API for a sports club/team platform called `SportsPlatform.Auth` / `Equipex`. It manages authentication, clubs, teams, invitations, seasons, events, attendance, player profiles, medical records, fitness records, announcements, coaching plans, lineups, match stats, basketball stats import, messaging, notifications, and search.

Important counts from the source:

| Area | Count |
| --- | ---: |
| Projects in solution | 3 |
| API controllers | 20 |
| Controller HTTP actions | 120 |
| Extra minimal health endpoint | 1 |
| Total HTTP endpoints | 121 |
| SignalR hubs | 1 |
| EF Core DbSet tables/entities exposed in AppDbContext | 35 |
| SQL migration scripts in `scripts/migrations` | 42 |
| Request/response DTO classes | 96 |

There is no global `/api` prefix for controller routes. Most routes start directly at `/auth`, `/clubs`, `/teams`, `/players`, `/messages`, and so on. The only built-in route with `/api` is `/api/health`.

## Solution Layout

| Project/folder | Purpose |
| --- | --- |
| `SportsPlatform.Auth.Api` | ASP.NET Core host. Contains `Program.cs`, controllers, middleware, SignalR hub, background service, and static file hosting. |
| `SportsPlatform.Auth.Core` | Domain entities, enums, request DTOs, response DTOs, and service interfaces. It is intentionally dependency-light. |
| `SportsPlatform.Auth.Infrastructure` | EF Core `AppDbContext`, entity configurations, SQL migration runner, and concrete services. |
| `scripts/migrations` | Numbered PostgreSQL SQL migrations for RLS, triggers, helper functions, schema fixes, and domain features. |
| `Dockerfile` | Multi-stage .NET 9 build/runtime image for the API. |
| `docker-compose.yml` | Runs API plus PostgreSQL 16 locally/containerized. |
| `SportsPlatform.Auth.Api/wwwroot` | Static files, uploads, and bundled frontend assets served by the API. |

## Technology Stack And Why It Is Used

| Technology | Used for | Why it fits this backend | Why not something else |
| --- | --- | --- | --- |
| .NET 9 / ASP.NET Core | Web API, dependency injection, middleware, auth, static hosting, SignalR | Strong typing, high performance, first-class auth/middleware pipeline, good fit for complex business rules and a C# solution | Node/FastAPI would work, but this codebase already benefits from C# types, EF Core, built-in DI, and compiled domain contracts |
| MVC controllers | HTTP endpoints | The API is organized as controller classes with attributes, which makes route grouping and auth metadata explicit | Minimal APIs are lighter, but this backend has many domain modules where controllers keep the surface easier to scan |
| EF Core | ORM and database access | Handles entity mapping, relationships, transactions, LINQ queries, migrations hook, and PostgreSQL enum mapping | Raw SQL everywhere would be harder to maintain; Dapper would be faster in places but would lose the central entity model |
| PostgreSQL 16 | Main database | The domain is relational: users, memberships, teams, events, messages, stats, documents. PostgreSQL also supports enums, RLS, triggers, and strong constraints | MongoDB would not naturally enforce membership relationships; SQL Server/MySQL could work but PostgreSQL RLS/enums are already used deeply |
| Npgsql | .NET PostgreSQL provider | Maps C# enums to PostgreSQL enums and powers EF Core PostgreSQL access | Generic providers would not support PostgreSQL-specific enum/RLS behavior as cleanly |
| Numbered SQL scripts | Advanced DB migrations | RLS policies, helper functions, triggers, and database-specific fixes are easier and clearer in SQL scripts | EF migrations alone are weaker for RLS/functions/triggers and database-native policies |
| JWT bearer auth | API authentication | Stateless access tokens are good for web/mobile clients; tokens carry user id, roles, clubs, teams, and admin claims | Cookie sessions are less convenient for mobile/API clients and would add server/session coupling |
| Refresh tokens | Long-lived session renewal | Short-lived access tokens reduce risk; refresh tokens allow re-authentication without logging in every 15 minutes | Long-lived JWTs would be simpler but harder to revoke safely |
| Google OAuth and Google mobile ID token validation | Social login | Supports browser OAuth redirect and mobile device sign-in | Password-only auth would hurt onboarding; custom OAuth handling would be riskier |
| BCrypt.Net-Next | Password hashing | BCrypt is a proven password hashing algorithm with salt/work factor support | Plain hashes or SHA would be unsafe for passwords |
| SignalR | Real-time notifications | Provides authenticated real-time push with groups per user and fallback transport handling | Polling would be simpler but wasteful and slower; raw WebSockets would require more custom plumbing |
| MailKit/MimeKit | SMTP invitation and critical notification emails | Modern SMTP/MIME support and async APIs | `System.Net.Mail` is older and less flexible |
| Local file storage in `wwwroot/uploads` | Club/team images, profile images, chat media, medical documents, event documents | Simple local development/deployment model; files are immediately served by the same API host | S3/Azure Blob would be better for scaling and durability, but would require external setup |
| ASP.NET Data Protection | OAuth/cookie cryptographic keys | Persists keys under `.keys` so OAuth/cookie auth remains stable across restarts | Ephemeral keys would invalidate auth cookies after restart |
| Hosted background service | Daily notification cleanup/reminders | Built into ASP.NET Core and runs in-process once per API instance | A separate worker/queue would scale better later but is more operational overhead now |
| Docker / Docker Compose | Local/container deployment | Reproducible .NET runtime plus PostgreSQL 16 | Manual local setup is easier to drift |
| External FastAPI extractor on port `8100` | Basketball PDF table extraction | The C# API delegates complex basketball PDF extraction to a specialized sidecar service | Reimplementing PDF extraction in the API would complicate the backend and require heavy parsing dependencies |
| `pdftotext`/Poppler path | Generic stats CSV/PDF preview | Existing service can convert uploaded PDFs to text for older stats import parsing | It is limited and depends on a local binary; CSV remains the built-in fallback |

## Runtime Startup Flow

`SportsPlatform.Auth.Api/Program.cs` builds the API in this order:

1. Configure console/debug logging.
2. Configure ASP.NET Data Protection and persist keys to `.keys`.
3. Read `ConnectionStrings:DefaultConnection`.
4. Build an Npgsql data source and map PostgreSQL enum types.
5. Register `AppDbContext` with EF Core/Npgsql.
6. Configure authentication:
   - JWT bearer is the default authenticate/challenge scheme.
   - Cookie auth is used for Google OAuth sign-in.
   - Google OAuth is configured with `Google:ClientId`, `Google:ClientSecret`, and callback path.
   - SignalR clients may pass `access_token` on `/hubs/notifications`.
7. Register authorization.
8. Register application services and background services.
9. Add SignalR.
10. Add controllers with camelCase JSON and string enum serialization.
11. Build the app.
12. On startup, run `db.Database.MigrateAsync()`.
13. Run pending numbered SQL scripts through `SqlMigrationRunner`.
14. Add exception handling middleware.
15. Serve static files from `wwwroot` and `wwwroot/dist`.
16. Run authentication.
17. Run `RlsMiddleware` to set PostgreSQL session variables.
18. Run authorization.
19. Map controllers.
20. Map SignalR hub `/hubs/notifications`.
21. Map public health route `/api/health`.
22. Map SPA fallback to `dist/index.html` if present.

## Request Pipeline

For a normal API request:

1. The client sends JSON, form data, query params, or file uploads.
2. `ExceptionHandlingMiddleware` wraps the request and converts unhandled exceptions into JSON.
3. Static files are checked before controller routing.
4. JWT authentication validates the bearer token when required.
5. `RlsMiddleware` opens the EF database connection and sets:
   - `app.user_id`
   - `app.is_admin`
   - `app.user_email`
6. ASP.NET authorization checks `[Authorize]` and `[AllowAnonymous]`.
7. The controller extracts the caller user id from `ClaimTypes.NameIdentifier`.
8. The controller calls a domain service, or in a few cases uses `AppDbContext` directly.
9. Services enforce business permissions, update the database, write files, send notifications, and send emails.
10. JSON responses use camelCase names and string enum values.
11. The RLS session settings are reset when the request completes.

## Authentication And Authorization

### Auth methods

The API supports:

- Local registration/login with email/password.
- Google browser OAuth redirect flow.
- Google mobile login using a Google ID token.
- JWT access tokens.
- Refresh tokens stored in the database.
- Logout by revoking a refresh token.

### JWT contents

`TokenService` includes these claims:

| Claim | Meaning |
| --- | --- |
| `sub` / `ClaimTypes.NameIdentifier` | User id |
| `email` | User email |
| `name` | User display name |
| `jti` | Token id |
| `ClaimTypes.Role` | Distinct roles |
| `is_admin` | `true` or `false` |
| `club:{clubId}` | Role within a club |
| `team:{teamId}` | Role within a team |

Access tokens default to 15 minutes. Refresh tokens default to 7 days.

### Roles

Roles are defined in `RoleNameType`:

| Role | Meaning in code |
| --- | --- |
| `Admin` | Global admin bypass in many services |
| `ClubManager` | Club owner/manager role |
| `TeamManager` | Team-level manager and, in some club membership checks, manager for team creation |
| `Coach` | Coaching plans, lineups, event plan attachment, stats visibility |
| `TeamAnalyst` | Stats recording/viewing |
| `TeamDoctor` | Medical records and medical notifications |
| `FitnessCoach` | Fitness records and player profile management |
| `Player` | Player-specific access to own data |

Authorization is mostly service-level. Controllers usually only require a valid JWT and pass the caller id to services. The service layer decides whether the user can view or mutate the domain object.

### Security observations

- `appsettings.json` currently contains real-looking database, JWT, Google, and SMTP secrets. Do not copy them into docs or commits again. Move them to environment variables, user-secrets, or a secret manager, then rotate them.
- There is no Swagger/OpenAPI setup in the API project, so this endpoint inventory is source-derived.
- There is no explicit CORS configuration. This is fine for same-origin static hosting, but cross-origin clients will need CORS configured.
- `ExceptionHandlingMiddleware` returns raw exception messages to clients. That is helpful in development but can leak internal details in production.
- File uploads are accepted in several places. Some endpoints validate extension/size; deeper content validation, antivirus scanning, and storage quotas would be needed for production hardening.

## Database And Persistence

### DbContext entities

`AppDbContext` exposes 35 DbSets:

| Area | DbSets |
| --- | --- |
| Auth/users | `Users`, `UserAuthProviders`, `RefreshTokens` |
| Organization | `Clubs`, `ClubMemberships`, `Teams`, `TeamMemberships`, `Invitations` |
| Players | `PlayerProfiles`, `PlayerTeams` |
| Scheduling | `Seasons`, `Events`, `EventExceptions`, `EventDocuments`, `EventPlans` |
| Attendance | `Attendances` |
| Medical/fitness | `MedicalRecords`, `MedicalDocumentRequests`, `FitnessRecords` |
| Communication | `Announcements`, `Conversations`, `ConversationParticipants`, `Messages`, `MessageReactions`, `MessageReadReceipts`, `AppNotifications` |
| Coaching | `CoachingPlans`, `CoachingLineups`, `CoachingLineupPlayers` |
| Stats | `PlayerGameStats`, `MatchStats`, `PlayerMatchStats`, `MatchAnalysisReports`, `MatchLineupAnalyses`, `MatchAnalysisDocuments` |

### PostgreSQL enums

These C# enums are mapped to PostgreSQL enum types:

- `AuthProviderType`
- `RoleNameType`
- `InvitationStatus`
- `MembershipStatus`
- `EventType`
- `AttendanceStatus`
- `AnnouncementPriority`
- `PlanVisibility`

### Migrations

Startup runs two migration systems:

1. `db.Database.MigrateAsync()` for EF Core migrations.
2. `SqlMigrationRunner.RunPendingMigrationsAsync()` for numbered SQL scripts.

The source tree currently has the SQL migration runner and 42 SQL scripts. No source EF migration class files are present; the SQL scripts are the main visible schema evolution mechanism in the repo.

The SQL runner:

- Ensures `_applied_sql_migrations` exists.
- Sorts `*.sql` scripts lexicographically.
- Runs only scripts not yet recorded.
- Executes each script inside a transaction.
- Records successful scripts in `_applied_sql_migrations`.

## Services

### Registered services

`Program.cs` registers:

| Interface | Implementation | Responsibility |
| --- | --- | --- |
| `ITokenService` | `TokenService` | JWT creation, refresh token lifecycle |
| `IFileStorageService` | `LocalFileStorageService` | Saves/deletes uploaded files under `wwwroot/uploads` |
| `IAuthService` | `AuthService` | Local and Google authentication |
| `ITeamService` | `TeamService` | Teams, categories, memberships |
| `IClubService` | `ClubService` | Clubs, logos, club members |
| `IInvitationService` | `InvitationService` | Club/team invitations and acceptance |
| `IEmailService` | `EmailService` | SMTP invitations and notification emails |
| `IPlayerService` | `PlayerService` | Player profiles and team player lists |
| `IEventService` | `EventService` | Seasons, events, recurring exceptions |
| `IAttendanceService` | `AttendanceService` | Event attendance |
| `IMedicalService` | `MedicalService` | Medical records, clearance, documents |
| `IFitnessService` | `FitnessService` | Fitness records |
| `IAnnouncementService` | `AnnouncementService` | Team announcements |
| `ICoachingPlanService` | `CoachingPlanService` | Plans and lineups |
| `IGameStatsService` | `GameStatsService` | Match stats, CSV/PDF preview, basketball stats |
| `IMessagingService` | `MessagingService` | Conversations, messages, reactions, media |
| `INotificationService` | `NotificationService` | Notification creation, listing, cleanup, reminders |
| `ISearchService` | `SearchService` | Search across visible teams/users/events/plans/etc. |
| `IRealtimeConnectionTracker` | `NotificationConnectionTracker` | Tracks SignalR user connections |
| `INotificationRealtimePublisher` | `SignalRNotificationPublisher` | Pushes SignalR notifications |
| hosted service | `NotificationMaintenanceService` | Daily cleanup and medical return reminders |

### Present but not exposed/registered

`IMatchAnalysisService` and `MatchAnalysisService` exist and read match analysis reports/summaries, and the database has match analysis entities. There is no controller route and no DI registration for `IMatchAnalysisService` in `Program.cs`, so it is not currently public API surface.

## Endpoint Inventory

Auth column values:

- `Anonymous`: no JWT required.
- `JWT`: bearer token required.

Input column values name the body DTO, query params, or form fields.

### Auth

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 1 | POST | `/auth/register` | Anonymous | `RegisterRequest` | Creates a local user and returns access/refresh tokens. |
| 2 | POST | `/auth/login` | Anonymous | `LoginRequest` | Logs in by email/phone plus password and returns tokens plus membership-scoped user info. |
| 3 | POST | `/auth/google/mobile` | Anonymous | `GoogleMobileLoginRequest` | Validates a Google ID token from a mobile client and returns API tokens. |
| 4 | GET | `/auth/google` | Anonymous | none | Starts browser Google OAuth by issuing a Google challenge. |
| 5 | GET | `/auth/google/result` | Anonymous | Google auth cookie/callback state | Completes Google OAuth result handling and redirects to the console/root with token data in query params. |
| 6 | POST | `/auth/complete-google-profile` | JWT | `CompleteGoogleProfileRequest` | Completes a Google user's missing profile fields. |
| 7 | POST | `/auth/refresh` | Anonymous | `RefreshTokenRequest` | Exchanges a valid refresh token for a new access token. |
| 8 | POST | `/auth/logout` | JWT | `RefreshTokenRequest` | Revokes a refresh token. |

### Clubs

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 9 | POST | `/clubs` | JWT | JSON `CreateClubRequest` or multipart fields plus optional `logo` | Creates a club for the caller. |
| 10 | POST | `/clubs/{clubId}/logo` | JWT | form file `logo` | Updates a club logo. |
| 11 | GET | `/clubs/my` | JWT | none | Lists clubs visible to the current user. |
| 12 | GET | `/clubs/{clubId}` | JWT | route `clubId` | Gets one club. |
| 13 | DELETE | `/clubs/{clubId}` | JWT | route `clubId` | Deletes/revokes a club according to service rules. |
| 14 | GET | `/clubs/{clubId}/members` | JWT | route `clubId` | Lists club members. |
| 15 | DELETE | `/clubs/{clubId}/members/{userId}` | JWT | route `clubId`, target `userId` | Removes a club member and revokes their active team memberships in that club. |
| 16 | POST | `/clubs/{clubId}/invitations` | JWT | `CreateInvitationRequest` | Creates a club-level invitation and sends invitation email. |
| 17 | GET | `/clubs/{clubId}/invitations` | JWT | route `clubId` | Lists club invitations. |
| 18 | DELETE | `/clubs/{clubId}/invitations/{invitationId}` | JWT | route ids | Cancels a club invitation. |

### Teams And Invitations

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 19 | GET | `/teams/categories` | Anonymous | none | Lists team categories and age ranges. |
| 20 | POST | `/clubs/{clubId}/teams` | JWT | JSON `CreateTeamRequest` or multipart fields plus optional `image` | Creates a team inside a club and may create its initial season. |
| 21 | GET | `/teams/my` | JWT | none | Lists teams for the current user. |
| 22 | GET | `/clubs/{clubId}/teams` | JWT | route `clubId` | Lists teams in a club. |
| 23 | GET | `/clubs/{clubId}/teams/{teamId}` | JWT | route ids | Gets one team. |
| 24 | GET | `/clubs/{clubId}/teams/{teamId}/members` | JWT | route ids | Lists team members. |
| 25 | DELETE | `/clubs/{clubId}/teams/{teamId}/members/{memberUserId}` | JWT | route ids | Removes a team member, with guard that a team keeps at least one active manager. |
| 26 | DELETE | `/clubs/{clubId}/teams/{teamId}` | JWT | route ids | Deletes/revokes a team. |
| 27 | POST | `/clubs/{clubId}/teams/{teamId}/invitations` | JWT | `CreateInvitationRequest` | Creates a team invitation and sends invitation email. |
| 28 | GET | `/clubs/{clubId}/teams/{teamId}/invitations` | JWT | route ids | Lists team invitations. |
| 29 | DELETE | `/clubs/{clubId}/teams/{teamId}/invitations/{invitationId}` | JWT | route ids | Cancels a team invitation. |
| 30 | GET | `/invitations/{token}` | JWT | invitation token | Gets invitation details by token. |
| 31 | GET | `/invitations/me` | JWT | none | Lists pending invitations for the current user. |
| 32 | POST | `/invitations/{token}/accept` | JWT | invitation token | Accepts an invitation and creates the proper club/team membership. |

### Users

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 33 | PUT | `/users/me` | JWT | `UpdateProfileRequest` | Updates current user's profile fields. |
| 34 | POST | `/users/me/profile-image` | JWT | form file `image` | Uploads/replaces current user's profile image. |

### Seasons, Events, Attendance, Event Documents, Event Plans

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 35 | GET | `/seasons` | JWT | none | Lists legacy/global seasons visible to caller. |
| 36 | GET | `/seasons/current` | JWT | none | Gets current legacy/global season for caller. |
| 37 | POST | `/seasons` | JWT | `CreateSeasonRequest` | Creates a legacy/global season. |
| 38 | GET | `/clubs/{clubId}/teams/{teamId}/seasons` | JWT | route ids | Lists seasons for a team. |
| 39 | GET | `/clubs/{clubId}/teams/{teamId}/seasons/current` | JWT | route ids | Gets current season for a team. |
| 40 | POST | `/clubs/{clubId}/teams/{teamId}/seasons` | JWT | `CreateSeasonRequest` | Creates a team-scoped season. |
| 41 | POST | `/clubs/{clubId}/teams/{teamId}/events` | JWT | `CreateEventRequest` | Creates an event in a team season. |
| 42 | GET | `/clubs/{clubId}/teams/{teamId}/events` | JWT | optional query `from`, `to` | Lists team events within an optional date range. |
| 43 | GET | `/clubs/{clubId}/teams/{teamId}/events/{eventId}` | JWT | route ids | Gets one event. |
| 44 | PUT | `/clubs/{clubId}/teams/{teamId}/events/{eventId}` | JWT | `UpdateEventRequest` | Updates event details and recurrence metadata. |
| 45 | DELETE | `/clubs/{clubId}/teams/{teamId}/events/{eventId}` | JWT | route ids | Deletes/cancels an event. |
| 46 | POST | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/cancel-instance` | JWT | `CancelEventInstanceRequest` | Cancels one instance of a recurring event. |
| 47 | POST | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/reschedule-instance` | JWT | `RescheduleEventInstanceRequest` | Reschedules one instance of a recurring event. |
| 48 | POST | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/attendance` | JWT | `RecordAttendanceRequest` | Records attendance entries for an event instance. |
| 49 | GET | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/attendance` | JWT | optional query `instanceDate` | Gets attendance for an event instance. |
| 50 | PUT | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/attendance/{playerUserId}` | JWT | `UpdateAttendanceRequest` | Updates one player's attendance for an event instance. |
| 51 | GET | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/attendance/me` | JWT | optional query `instanceDate` | Gets current player's own attendance for the event. |
| 52 | GET | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/documents` | JWT | route ids | Lists documents attached to an event. |
| 53 | POST | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/documents` | JWT | form file `file`, optional `description` | Uploads an event document. |
| 54 | GET | `/events/documents/{documentId}/download` | JWT | route `documentId` | Downloads an event document if caller can view the team. |
| 55 | DELETE | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/documents/{documentId}` | JWT | route ids | Soft-deletes an event document. |
| 56 | GET | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/plans` | JWT | route ids | Lists coaching plans linked to an event. |
| 57 | POST | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/plans/{planId}` | JWT | route ids | Attaches a caller-owned team-visible coaching plan to an event. |
| 58 | DELETE | `/clubs/{clubId}/teams/{teamId}/events/{eventId}/plans/{planId}` | JWT | route ids | Detaches a coaching plan from an event. |

### Players, Medical, Fitness

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 59 | GET | `/players/me/profile` | JWT | none | Gets current user's player profile. |
| 60 | GET | `/clubs/{clubId}/teams/{teamId}/players` | JWT | route ids | Lists players on a team. |
| 61 | GET | `/clubs/{clubId}/teams/{teamId}/players/{playerUserId}/profile` | JWT | route ids | Gets a player profile within a team. |
| 62 | POST | `/clubs/{clubId}/teams/{teamId}/players/{playerUserId}/profile` | JWT | `UpsertPlayerProfileRequest` | Creates or updates a player's sports profile. |
| 63 | POST | `/clubs/{clubId}/teams/{teamId}/players/{playerUserId}/medical` | JWT | `CreateMedicalRecordRequest` | Creates a medical record for a player. |
| 64 | PUT | `/clubs/{clubId}/teams/{teamId}/medical/{recordId}` | JWT | `UpdateMedicalRecordRequest` | Updates a medical record. |
| 65 | GET | `/clubs/{clubId}/teams/{teamId}/players/{playerUserId}/medical` | JWT | route ids | Lists medical records for a player. |
| 66 | DELETE | `/clubs/{clubId}/teams/{teamId}/medical/{recordId}` | JWT | route ids | Deletes a medical record. |
| 67 | POST | `/clubs/{clubId}/teams/{teamId}/medical/{recordId}/delete` | JWT | route ids | POST alias for deleting a medical record. |
| 68 | PATCH | `/clubs/{clubId}/teams/{teamId}/medical/{recordId}/clearance` | JWT | `UpdateMedicalClearanceRequest` | Updates medical clearance status. |
| 69 | POST | `/clubs/{clubId}/teams/{teamId}/medical/{recordId}/document-requests` | JWT | `RequestMedicalDocumentRequest` | Requests a document from the player for a medical record. |
| 70 | POST | `/players/me/medical/document-requests/{requestId}/upload` | JWT | form file `file` | Current player uploads requested medical document. |
| 71 | GET | `/medical/document-requests/{requestId}/download` | JWT | route `requestId` | Downloads a medical document if permitted. |
| 72 | GET | `/players/me/medical` | JWT | none | Gets current player's medical records. |
| 73 | POST | `/clubs/{clubId}/teams/{teamId}/players/{playerUserId}/fitness` | JWT | `CreateFitnessRecordRequest` | Fitness coach/admin creates a fitness record. |
| 74 | GET | `/clubs/{clubId}/teams/{teamId}/players/{playerUserId}/fitness` | JWT | route ids | Gets a player's fitness records. |
| 75 | GET | `/players/me/fitness` | JWT | none | Gets current player's fitness records. |

### Coaching Plans, Lineups, Announcements

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 76 | POST | `/clubs/{clubId}/teams/{teamId}/plans` | JWT | `CreatePlanRequest` | Creates a coaching plan. |
| 77 | GET | `/clubs/{clubId}/teams/{teamId}/plans` | JWT | route ids | Lists coaching plans visible to the caller. |
| 78 | GET | `/clubs/{clubId}/teams/{teamId}/plans/{planId}` | JWT | route ids | Gets one coaching plan. |
| 79 | PUT | `/clubs/{clubId}/teams/{teamId}/plans/{planId}` | JWT | `UpdatePlanRequest` | Updates a coaching plan. |
| 80 | DELETE | `/clubs/{clubId}/teams/{teamId}/plans/{planId}` | JWT | route ids | Deletes a coaching plan. |
| 81 | POST | `/clubs/{clubId}/teams/{teamId}/lineups` | JWT | `CreateLineupRequest` | Creates a lineup with player assignments. |
| 82 | GET | `/clubs/{clubId}/teams/{teamId}/lineups` | JWT | route ids | Lists team lineups. |
| 83 | GET | `/clubs/{clubId}/teams/{teamId}/lineups/{lineupId}` | JWT | route ids | Gets one lineup. |
| 84 | PUT | `/clubs/{clubId}/teams/{teamId}/lineups/{lineupId}` | JWT | `UpdateLineupRequest` | Updates a lineup. |
| 85 | DELETE | `/clubs/{clubId}/teams/{teamId}/lineups/{lineupId}` | JWT | route ids | Deletes a lineup. |
| 86 | POST | `/clubs/{clubId}/teams/{teamId}/announcements` | JWT | JSON `CreateAnnouncementRequest` | Creates an announcement without file upload. |
| 87 | POST | `/clubs/{clubId}/teams/{teamId}/announcements` | JWT | multipart `CreateAnnouncementRequest`, optional `image` | Creates an announcement with optional image upload. |
| 88 | GET | `/clubs/{clubId}/teams/{teamId}/announcements` | JWT | route ids | Lists team announcements. |
| 89 | PUT | `/clubs/{clubId}/teams/{teamId}/announcements/{announcementId}` | JWT | JSON `UpdateAnnouncementRequest` | Updates an announcement without file upload. |
| 90 | PUT | `/clubs/{clubId}/teams/{teamId}/announcements/{announcementId}` | JWT | multipart `UpdateAnnouncementRequest`, optional `image` | Updates an announcement with optional image upload. |
| 91 | POST | `/clubs/{clubId}/teams/{teamId}/announcements/{announcementId}/update` | JWT | JSON `UpdateAnnouncementRequest` | POST alias for updating an announcement. |
| 92 | POST | `/clubs/{clubId}/teams/{teamId}/announcements/{announcementId}/update` | JWT | multipart `UpdateAnnouncementRequest`, optional `image` | POST multipart alias for updating an announcement. |
| 93 | DELETE | `/clubs/{clubId}/teams/{teamId}/announcements/{announcementId}` | JWT | route ids | Deletes an announcement. |
| 94 | POST | `/clubs/{clubId}/teams/{teamId}/announcements/{announcementId}/delete` | JWT | route ids | POST alias for deleting an announcement. |

### Stats

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 95 | POST | `/clubs/{clubId}/teams/{teamId}/stats` | JWT | `CreateMatchStatsRequest` | Creates manual match/team/player stats. |
| 96 | POST | `/clubs/{clubId}/teams/{teamId}/stats/upload` | JWT | multipart `eventId`, `file` CSV/PDF, 20 MB limit | Parses CSV/PDF and returns a save preview. |
| 97 | GET | `/clubs/{clubId}/teams/{teamId}/stats` | JWT | route ids | Gets team aggregate stats. |
| 98 | GET | `/clubs/{clubId}/teams/{teamId}/stats/matches` | JWT | route ids | Gets match stats history. |
| 99 | GET | `/clubs/{clubId}/teams/{teamId}/stats/matches/{eventId}` | JWT | route ids | Gets stats for one event/match. |
| 100 | GET | `/clubs/{clubId}/teams/{teamId}/stats/players/{playerUserId}` | JWT | route ids | Gets one player's aggregate stats. |
| 101 | GET | `/clubs/{clubId}/teams/{teamId}/stats/players/{playerUserId}/matches` | JWT | route ids | Gets one player's match-by-match stats. |
| 102 | POST | `/clubs/{clubId}/teams/{teamId}/stats/basketball/extract` | JWT | multipart PDF `file`, 20 MB limit | Sends PDF to external FastAPI extractor at `http://localhost:8100/extract` and returns extracted rows. |
| 103 | POST | `/clubs/{clubId}/teams/{teamId}/stats/basketball` | JWT | `CreateBasketballStatsRequest` | Creates basketball stats manually. |
| 104 | POST | `/clubs/{clubId}/teams/{teamId}/stats/basketball/confirm` | JWT | `ConfirmBasketballUploadRequest` | Saves previously extracted basketball rows after confirmation/mapping. |
| 105 | GET | `/clubs/{clubId}/teams/{teamId}/stats/basketball` | JWT | route ids | Gets basketball aggregate stats. |

### Messaging, Notifications, Search, Health, Realtime

| # | Verb | Path | Auth | Input | What it does |
| ---: | --- | --- | --- | --- | --- |
| 106 | POST | `/messages/conversations` | JWT | `CreateConversationRequest` | Creates a direct or group conversation. |
| 107 | GET | `/messages/conversations` | JWT | none | Lists current user's conversations. |
| 108 | GET | `/messages/conversations/{conversationId}/messages` | JWT | query `page=1`, `pageSize=50` | Gets paged messages in a conversation. |
| 109 | POST | `/messages/conversations/{conversationId}/messages` | JWT | `SendMessageRequest` | Sends a text/location/media-reference message. |
| 110 | POST | `/messages/conversations/{conversationId}/read` | JWT | route `conversationId` | Marks a conversation as read by current user. |
| 111 | PUT | `/messages/messages/{messageId}` | JWT | `EditMessageRequest` | Edits a message. |
| 112 | DELETE | `/messages/messages/{messageId}` | JWT | route `messageId` | Deletes a message. |
| 113 | POST | `/messages/messages/{messageId}/reactions` | JWT | `SendReactionRequest` | Adds a reaction to a message. |
| 114 | DELETE | `/messages/messages/{messageId}/reactions/{emoji}` | JWT | route `messageId`, `emoji` | Removes a reaction. |
| 115 | POST | `/messages/conversations/{conversationId}/media` | JWT | form file `file` | Uploads chat media and sends it as a media message. |
| 116 | GET | `/notifications` | JWT | query `page=1`, `pageSize=30`, `unreadOnly=false` | Lists notifications for current user. |
| 117 | GET | `/notifications/unread-count` | JWT | none | Returns current user's unread notification count. |
| 118 | POST | `/notifications/{notificationId}/read` | JWT | route `notificationId` | Marks one notification as read. |
| 119 | POST | `/notifications/read-all` | JWT | none | Marks all current user's notifications as read. |
| 120 | GET | `/search` | JWT | query `q`, `type=all`, `page=1`, `pageSize=30` | Searches visible teams, users, events, plans, announcements, and stats. |
| 121 | GET | `/api/health` | Anonymous | none | Returns service name, `running` status, and UTC timestamp. |

Realtime hub:

| Hub path | Auth | Client token behavior | Server-to-client event |
| --- | --- | --- | --- |
| `/hubs/notifications` | JWT | Bearer token or `access_token` query parameter | `notificationReceived` |

## Request DTO Reference

These are the request body shapes used by the endpoints above. JSON names are serialized as camelCase.

| DTO | Fields |
| --- | --- |
| `RegisterRequest` | `email`, `password`, `name`, `username?`, `phoneNumber?`, `bio?`, `dob?` |
| `LoginRequest` | `emailOrPhone`, `password` |
| `GoogleMobileLoginRequest` | `idToken` |
| `CompleteGoogleProfileRequest` | `name`, `dob` |
| `RefreshTokenRequest` | `refreshToken` |
| `UpdateProfileRequest` | `name?`, `username?`, `bio?`, `dob?`, `phoneNumber?`, `yearsOfExperience?` |
| `CreateClubRequest` | `name`, `logoUrl?`, `location?`, `locationLatitude?`, `locationLongitude?` |
| `CreateTeamRequest` | `teamName`, `categoryId`, `seasonLabel`, `seasonStartDate?`, `seasonEndDate?`, `imageUrl?` |
| `CreateInvitationRequest` | `email`, `roleName`, `playerPosition?`, `jerseyNumber?` |
| `CreateSeasonRequest` | `label`, `startDate`, `endDate`, `isCurrent` |
| `CreateEventRequest` | `seasonId`, `title`, `eventType`, `startAt`, `endAt?`, `location?`, `locationLatitude?`, `locationLongitude?`, `description?`, `timezone?`, `recurrenceRule?`, `recurrenceEndDate?` |
| `UpdateEventRequest` | `title`, `eventType`, `startAt`, `endAt?`, `location?`, `locationLatitude?`, `locationLongitude?`, `description?`, `timezone?`, `recurrenceRule?`, `recurrenceEndDate?` |
| `CancelEventInstanceRequest` | `originalDate`, `notes?` |
| `RescheduleEventInstanceRequest` | `originalDate`, `newStartAt`, `newEndAt?`, `notes?` |
| `RecordAttendanceRequest` | `instanceDate`, `records` |
| `AttendanceEntryRequest` | `playerUserId`, `status`, `notes?` |
| `UpdateAttendanceRequest` | `instanceDate`, `status`, `notes?` |
| `UpsertPlayerProfileRequest` | `position`, `jerseyNumber?`, `height?`, `weight?` |
| `CreateMedicalRecordRequest` | `recordDate?`, `injuryType?`, `diagnosis?`, `expectedReturnDate?`, `recoveryTips?` |
| `UpdateMedicalRecordRequest` | `recordDate?`, `injuryType?`, `diagnosis?`, `expectedReturnDate?`, `recoveryTips?` |
| `UpdateMedicalClearanceRequest` | `isCleared` |
| `RequestMedicalDocumentRequest` | `documentName`, `note?` |
| `CreateFitnessRecordRequest` | `testDate?`, `height?`, `weight?`, `bmi?`, `bodyFatPct?`, `speedTestResult?`, `enduranceScore?`, `customTestName?`, `customTestResult?` |
| `CreateAnnouncementRequest` | `title`, `content`, `imageUrl?`, `priority` |
| `UpdateAnnouncementRequest` | `title`, `content`, `imageUrl?`, `priority` |
| `CreatePlanRequest` | `title`, `description?`, `content`, `visibility` |
| `UpdatePlanRequest` | `title`, `description?`, `content`, `visibility` |
| `CreateLineupRequest` | `eventId?`, `title`, `formation?`, `gameModel?`, `tacticalNotes?`, `visibility`, `players` |
| `UpdateLineupRequest` | Same fields as `CreateLineupRequest` |
| `LineupPlayerRequest` | `playerUserId`, `position`, `unit`, `sortOrder`, `instructions?` |
| `CreateMatchStatsRequest` | `eventId`, `opponentName?`, `teamScore?`, `opponentScore?`, `result?`, `venue?`, `competitionName?`, `possessionPercent?`, `totalGoals?`, `totalAssists?`, `shotsOnTarget?`, `totalShots?`, `passesCompleted?`, `passesAttempted?`, `passAccuracy?`, `tackles?`, `interceptions?`, `yellowCards?`, `redCards?`, `notes?`, `playerStats` |
| `CreatePlayerMatchStatsRequest` | `playerUserId`, `minutesPlayed?`, `goals?`, `assists?`, `shotsOnTarget?`, `totalShots?`, `passesCompleted?`, `passesAttempted?`, `passAccuracy?`, `tackles?`, `interceptions?`, `yellowCards?`, `redCards?`, `rating?`, `notes?` |
| `CreateBasketballStatsRequest` | `eventId`, `category`, `opponentName?`, `teamScore?`, `opponentScore?`, `venue?`, `competitionName?`, `gameNo?`, `matchup?`, shooting fields, rebound fields, `assists?`, `turnovers?`, `steals?`, `blocks?`, `personalFouls?`, `foulsDrawn?`, `efficiency?`, `points?`, `minutes?`, `notes?`, `playerStats` |
| `CreateBasketballPlayerStatsRequest` | `playerUserId?`, `playerName?`, `playerNo?`, `status?`, `isStarter`, `isCaptain`, `minutes?`, shooting fields, rebound fields, `assists?`, `turnovers?`, `steals?`, `blocks?`, `personalFouls?`, `foulsDrawn?`, `efficiency?`, `points?`, `notes?` |
| `ConfirmBasketballUploadRequest` | `eventId`, `category`, `rows` |
| `BasketballExtractedRow` | extraction metadata, team/game fields, player fields, shooting/rebound/counting stat fields |
| `CreateConversationRequest` | `participantUserIds`, `name?`, `isGroup` |
| `SendMessageRequest` | `content`, `messageType?`, `mediaUrl?`, `mediaFileName?`, `locationLatitude?`, `locationLongitude?`, `locationLabel?` |
| `EditMessageRequest` | `content` |
| `SendReactionRequest` | `emoji` |
| `CreateNotificationRequest` | `actorUserId?`, `clubId?`, `teamId?`, `type`, `priority`, `deliveryPolicy`, `title`, `body`, `targetType?`, `targetId?`, `targetRoute?`, `metadataJson?`, `uniqueKey?` |

## Response DTO Families

The API returns response DTOs from `SportsPlatform.Auth.Core/DTOs/Response`, including:

| Family | DTOs |
| --- | --- |
| Auth/user | `AuthResponse`, `UserInfoDto`, `UserClubInfoDto`, `UserTeamInfoDto` |
| Clubs/teams | `ClubDto`, `ClubSummaryDto`, `ClubMemberDto`, `TeamDto`, `TeamMemberDto`, `TeamCategoryDto`, `ManagerSummaryDto` |
| Invitations | `InvitationDto`, `InvitationAcceptResultDto` |
| Seasons/events | `SeasonDto`, `EventDto`, `EventExceptionDto` |
| Attendance | `AttendanceDto` |
| Medical/fitness | `MedicalRecordDto`, `MedicalDocumentRequestDto`, `MedicalDocumentDownloadDto`, `FitnessRecordDto` |
| Announcements | `AnnouncementDto` |
| Coaching | `PlanDto`, `LineupDto`, lineup player DTOs |
| Stats | `MatchStatsDto`, `TeamStatsAggregateDto`, `PlayerStatsAggregateDto`, `StatsUploadPreviewDto`, basketball stats DTOs |
| Messaging | `ConversationDto`, `ParticipantDto`, `MessageDto`, `MessageReactionDto`, `MessageSeenByDto` |
| Notifications/search | `NotificationDto`, `NotificationListDto`, `UnreadCountDto`, `SearchResponseDto`, `SearchResultDto` |
| Match analysis | `MatchAnalysisReportDto`, `MatchLineupAnalysisDto`, `MatchAnalysisDocumentDto`, `MatchAnalysisSummaryDto` |

## Domain Rules Observed In Services

| Domain | Important service rules |
| --- | --- |
| Auth | Registration requires DOB, unique email, and unique username if provided. Passwords are BCrypt hashed. Refresh tokens are persisted and revocable. |
| Clubs | A user can create only one club. Club creator/admin can delete a club and remove members. Removing a club member also revokes active team memberships inside the club. |
| Teams | Team creation requires valid category and season details. Teams must keep at least one active team manager. Removing a player also affects player-team/profile links. |
| Invitations | Club/team managers/admins create invitations. Player constraints prevent players from holding multiple active clubs/teams/roles. Player age is checked against team category min/max when accepting. |
| Events | Team managers/admins create/update/delete events and manage recurring exceptions. Event changes can notify team members. Dates are normalized to UTC. |
| Attendance | Attendance is scoped to event instances via `instanceDate`. |
| Medical | Medical actions are role-controlled. Document requests trigger notifications. Uploaded documents are stored under `uploads/medical-documents`. |
| Fitness | Only fitness coach/admin can create fitness records. Players can view their own records; staff can view team records. Custom test name/result are validated together. |
| Announcements | Announcements notify team members and may include uploaded images. POST aliases exist for update/delete to support clients that cannot use PUT/DELETE. |
| Plans/lineups | Plans have visibility, and event-plan attachment requires a coach/admin and a team-visible plan owned by the caller. |
| Stats | Analysts/coaches/managers/club managers/admins can record stats. Players can only view their own player stats unless staff/admin. Match/training event type is required for stats entry. |
| Messaging | Users create conversations, send/edit/delete messages, mark read, add/remove reactions, and upload chat media. |
| Notifications | Realtime notifications use SignalR; critical email fallback is supported. Daily maintenance deletes old notifications and sends medical return reminders. |
| Search | Search is filtered by teams visible to the caller and respects plan/stat visibility rules. |

## File Upload And Storage Paths

| Feature | Storage behavior |
| --- | --- |
| Club logos | `LocalFileStorageService`, category `clubs` |
| Team images | `LocalFileStorageService`, category likely `teams` through service layer |
| User profile images | `LocalFileStorageService`, category `users` |
| Announcement images | `LocalFileStorageService`, announcement category through service layer |
| Medical documents | `wwwroot/uploads/medical-documents` |
| Chat media | `wwwroot/uploads/chat-media` |
| Event documents | `wwwroot/uploads/events/{eventId}` |

`LocalFileStorageService` sanitizes category and file name, prefixes stored names with GUIDs, and only deletes paths under `/uploads`.

## Deployment And Configuration

### Docker

`Dockerfile`:

- Builds using `mcr.microsoft.com/dotnet/sdk:9.0`.
- Restores/publishes the API project.
- Runs on `mcr.microsoft.com/dotnet/aspnet:9.0`.
- Exposes port `5122`.
- Starts `SportsPlatform.Auth.Api.dll`.

`docker-compose.yml`:

- Runs `api` on host port `5122`.
- Runs PostgreSQL 16 on host port `5432`.
- Sets `ASPNETCORE_URLS=http://+:5122`.
- Provides `ConnectionStrings__DefaultConnection` pointed at the `db` service.

### Required configuration keys

Do not store real values in source control.

| Key | Purpose |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string |
| `Jwt:Secret` | HMAC signing secret |
| `Jwt:Issuer` | JWT issuer |
| `Jwt:Audience` | JWT audience |
| `Jwt:ExpiresInMinutes` | Access token lifetime |
| `Jwt:RefreshTokenExpiryDays` | Refresh token lifetime |
| `Google:ClientId` | Google OAuth client id |
| `Google:ClientSecret` | Google OAuth client secret |
| `Google:CallbackPath` | Google OAuth callback path |
| `Email:SmtpHost` | SMTP host |
| `Email:SmtpPort` | SMTP port |
| `Email:SenderEmail` | SMTP sender |
| `Email:SenderName` | Display sender |
| `Email:Password` | SMTP password/app password |

## Operational Notes

- Start PostgreSQL before the API; startup migrations require a database connection.
- The basketball extraction endpoint requires an external extractor at `http://localhost:8100/extract`.
- The generic stats upload preview for PDFs requires `pdftotext` to be installed and discoverable on the host.
- SignalR clients connect to `/hubs/notifications` and can pass `access_token` as a query parameter.
- Static frontend files are served from `wwwroot` and `wwwroot/dist`; unknown routes fall back to the SPA if `dist/index.html` exists.
- Because uploaded files are on local disk, horizontal scaling needs shared storage or object storage.
- Because background notification maintenance runs in-process, multiple API replicas would each run the daily job unless guarded.

## Quick API Count By Controller

| Controller | HTTP actions |
| --- | ---: |
| `AnnouncementController` | 9 |
| `AttendanceController` | 4 |
| `AuthController` | 8 |
| `ClubController` | 10 |
| `CoachingPlanController` | 5 |
| `EventController` | 13 |
| `EventDocumentController` | 4 |
| `EventPlanController` | 3 |
| `FitnessController` | 3 |
| `GameStatsController` | 11 |
| `InvitationController` | 3 |
| `LineupController` | 5 |
| `MedicalController` | 10 |
| `MessagingController` | 10 |
| `NotificationController` | 4 |
| `PlayerController` | 4 |
| `SearchController` | 1 |
| `TeamController` | 8 |
| `TeamInvitationController` | 3 |
| `UserController` | 2 |
| Extra `/api/health` minimal endpoint | 1 |

Total: 121 HTTP endpoints.
