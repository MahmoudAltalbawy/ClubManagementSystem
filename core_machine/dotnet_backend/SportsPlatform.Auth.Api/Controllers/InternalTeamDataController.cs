using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Controllers;

/// <summary>
/// Service-to-service endpoints the chatbot/prediction microservice uses to PULL
/// non-PDF, live team data (roster, injuries, availability, schedule, attendance,
/// fitness, coach-recorded stats, and coaching plans) that can't come from match PDFs.
/// Like <see cref="InternalMatchStatsController"/>, these are NOT for end users: they are
/// guarded by the shared bearer service token (not the user JWT), so we skip the
/// per-caller authorization that <c>TeamService</c> applies and scope strictly by teamId.
/// Wire format is snake_case to match the microservice.
///
/// Read-only by design: the chatbot reads and advises, it never mutates team data.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/teams")]
public class InternalTeamDataController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public InternalTeamDataController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    // ---------------------------------------------------------------------
    // Roster + current injuries
    // ---------------------------------------------------------------------

    /// <summary>Current roster for a team, with position, jersey, and uncleared-injury flags + count.</summary>
    [HttpGet("{teamId:guid}/roster")]
    public async Task<IActionResult> GetRoster(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var players = await LoadPlayersAsync(teamId);
        var injuries = await LoadUnclearedInjuryCountsAsync(teamId);

        var members = players.Select(p =>
        {
            var dto = new InternalRosterMemberDto
            {
                UserId = p.UserId,
                Name = p.Name,
                Role = p.Role,
                Position = p.Position,
                JerseyNumber = p.JerseyNumber,
                Height = p.Height,
                Weight = p.Weight,
            };
            if (p.PlayerId is Guid pid && injuries.TryGetValue(pid, out var inj))
            {
                dto.IsInjured = true;
                dto.InjuryType = inj.LatestType;
                dto.InjuryCount = inj.Count;
            }
            return dto;
        }).ToList();

        return Ok(new InternalRosterResponse { TeamId = teamId, Members = members });
    }

    // ---------------------------------------------------------------------
    // Active injuries (uncleared medical records)
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/injuries")]
    public async Task<IActionResult> GetInjuries(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var nameByPlayerId = (await LoadPlayersAsync(teamId))
            .Where(p => p.PlayerId is not null)
            .ToDictionary(p => p.PlayerId!.Value, p => p.Name);

        var records = await _db.MedicalRecords.AsNoTracking()
            .Where(mr => mr.TeamId == teamId && !mr.IsCleared)
            .OrderByDescending(mr => mr.RecordDate)
            .Select(mr => new
            {
                mr.PlayerId,
                mr.InjuryType,
                mr.Diagnosis,
                mr.RecordDate,
                mr.ExpectedReturnDate,
                mr.RecoveryTips,
                mr.DoctorUserId,
                mr.CreatedBy,
            })
            .ToListAsync();

        var recorderIds = records
            .Select(r => r.DoctorUserId ?? r.CreatedBy)
            .Where(id => id is not null)
            .Select(id => id!.Value);
        var recorderNames = await LoadUserNamesAsync(recorderIds);

        var injuries = records.Select(r =>
        {
            var recorderId = r.DoctorUserId ?? r.CreatedBy;
            return new InternalInjuryDto
            {
                PlayerId = r.PlayerId,
                Name = nameByPlayerId.TryGetValue(r.PlayerId, out var n) ? n : null,
                InjuryType = r.InjuryType,
                Diagnosis = r.Diagnosis,
                RecordDate = r.RecordDate,
                ExpectedReturnDate = r.ExpectedReturnDate,
                RecoveryTips = r.RecoveryTips,
                RecordedByName = recorderId is Guid rid && recorderNames.TryGetValue(rid, out var rn) ? rn : null,
            };
        }).ToList();

        return Ok(new InternalInjuriesResponse { TeamId = teamId, Injuries = injuries });
    }

    // ---------------------------------------------------------------------
    // Availability — the inference-time feed for the prediction model.
    // A player is unavailable if they have any uncleared medical record.
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/availability")]
    public async Task<IActionResult> GetAvailability(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var players = await LoadPlayersAsync(teamId);
        var injuries = await LoadUnclearedInjuryCountsAsync(teamId);

        var availability = players
            .Where(p => p.Role == RoleNameType.Player.ToString())
            .Select(p =>
            {
                var injured = p.PlayerId is Guid pid && injuries.ContainsKey(pid);
                string? reason = null;
                if (injured && p.PlayerId is Guid pid2 && injuries.TryGetValue(pid2, out var inj))
                    reason = string.IsNullOrWhiteSpace(inj.LatestType) ? "injured" : inj.LatestType;
                return new InternalAvailabilityDto
                {
                    UserId = p.UserId,
                    Name = p.Name,
                    Position = p.Position,
                    JerseyNumber = p.JerseyNumber,
                    Available = !injured,
                    Reason = reason,
                };
            })
            .ToList();

        return Ok(new InternalAvailabilityResponse { TeamId = teamId, Players = availability });
    }

    // ---------------------------------------------------------------------
    // Upcoming schedule
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/schedule")]
    public async Task<IActionResult> GetSchedule(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var now = DateTime.UtcNow;
        var events = await _db.Events.AsNoTracking()
            .Where(e => e.TeamId == teamId && e.DeletedAt == null && e.StartAt >= now)
            .OrderBy(e => e.StartAt)
            .Take(50)
            .Select(e => new InternalScheduleEventDto
            {
                EventId = e.EventId,
                Title = e.Title,
                EventType = e.EventType.ToString(),
                StartAt = e.StartAt,
                EndAt = e.EndAt,
                Location = e.Location,
            })
            .ToListAsync();

        return Ok(new InternalScheduleResponse { TeamId = teamId, Events = events });
    }

    // ---------------------------------------------------------------------
    // Attendance rate per player over a rolling window (default 90 days)
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/attendance")]
    public async Task<IActionResult> GetAttendance(Guid teamId, [FromQuery] int days = 90)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        if (days <= 0) days = 90;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));

        var nameByPlayerId = (await LoadPlayersAsync(teamId))
            .Where(p => p.PlayerId is not null)
            .ToDictionary(p => p.PlayerId!.Value, p => p.Name);

        var rows = await _db.Attendances.AsNoTracking()
            .Where(a => a.Event.TeamId == teamId
                        && a.Event.DeletedAt == null
                        && a.InstanceDate >= cutoff)
            .Select(a => new { a.PlayerId, a.Status, a.RecordedByUserId, a.RecordedAt })
            .ToListAsync();

        var recorderNames = await LoadUserNamesAsync(
            rows.Where(r => r.RecordedByUserId is not null).Select(r => r.RecordedByUserId!.Value));

        var attendance = rows
            .GroupBy(r => r.PlayerId)
            .Select(g =>
            {
                var total = g.Count();
                var present = g.Count(x => x.Status == AttendanceStatus.Present || x.Status == AttendanceStatus.Late);
                var latestRecorderId = g.OrderByDescending(x => x.RecordedAt)
                    .Select(x => x.RecordedByUserId)
                    .FirstOrDefault(id => id is not null);
                return new InternalAttendanceDto
                {
                    PlayerId = g.Key,
                    Name = nameByPlayerId.TryGetValue(g.Key, out var n) ? n : null,
                    Present = present,
                    Total = total,
                    Rate = total == 0 ? 0 : Math.Round((double)present / total, 3),
                    RecordedByName = latestRecorderId is Guid rid && recorderNames.TryGetValue(rid, out var rn) ? rn : null,
                };
            })
            .OrderBy(d => d.Name)
            .ToList();

        return Ok(new InternalAttendanceResponse { TeamId = teamId, WindowDays = days, Players = attendance });
    }

    // ---------------------------------------------------------------------
    // Latest fitness record per player
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/fitness")]
    public async Task<IActionResult> GetFitness(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var nameByPlayerId = (await LoadPlayersAsync(teamId))
            .Where(p => p.PlayerId is not null)
            .ToDictionary(p => p.PlayerId!.Value, p => p.Name);

        var records = await _db.FitnessRecords.AsNoTracking()
            .Where(f => f.TeamId == teamId)
            .OrderByDescending(f => f.TestDate)
            .Select(f => new
            {
                f.PlayerId,
                f.TestDate,
                f.Height,
                f.Weight,
                f.Bmi,
                f.BodyFatPct,
                f.SpeedTestResult,
                f.EnduranceScore,
                f.CustomTestName,
                f.CustomTestResult,
                RecorderId = f.FitnessUserId ?? f.CreatedBy,
            })
            .ToListAsync();

        var recorderNames = await LoadUserNamesAsync(
            records.Where(r => r.RecorderId is not null).Select(r => r.RecorderId!.Value));

        var latest = records
            .GroupBy(r => r.PlayerId)
            .Select(g => g.First()) // already ordered by TestDate desc
            .Select(r => new InternalFitnessDto
            {
                PlayerId = r.PlayerId,
                Name = nameByPlayerId.TryGetValue(r.PlayerId, out var n) ? n : null,
                TestDate = r.TestDate,
                Height = r.Height,
                Weight = r.Weight,
                Bmi = r.Bmi,
                BodyFatPct = r.BodyFatPct,
                SpeedTestResult = r.SpeedTestResult,
                EnduranceScore = r.EnduranceScore,
                CustomTestName = r.CustomTestName,
                CustomTestResult = r.CustomTestResult,
                RecordedByName = r.RecorderId is Guid rid && recorderNames.TryGetValue(rid, out var rn) ? rn : null,
            })
            .OrderBy(d => d.Name)
            .ToList();

        return Ok(new InternalFitnessResponse { TeamId = teamId, Players = latest });
    }

    // ---------------------------------------------------------------------
    // Coach-recorded player stats (DB-native; distinct from PDF box scores)
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/player-stats")]
    public async Task<IActionResult> GetPlayerStats(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var rows = await _db.PlayerGameStats.AsNoTracking()
            .Where(s => s.TeamId == teamId)
            .Select(s => new
            {
                s.PlayerUserId,
                Name = s.Player.Name,
                s.Goals,
                s.Assists,
                s.MinutesPlayed,
                s.YellowCards,
                s.RedCards,
                s.Rating,
                s.RecordedBy,
            })
            .ToListAsync();

        var recorderNames = await LoadUserNamesAsync(rows.Select(r => r.RecordedBy));

        var stats = rows
            .GroupBy(r => new { r.PlayerUserId, r.Name })
            .Select(g =>
            {
                var latestRecorderId = g.Select(x => x.RecordedBy).FirstOrDefault(id => id != Guid.Empty);
                return new InternalPlayerStatDto
                {
                    UserId = g.Key.PlayerUserId,
                    Name = g.Key.Name,
                    Matches = g.Count(),
                    Goals = g.Sum(x => x.Goals ?? 0),
                    Assists = g.Sum(x => x.Assists ?? 0),
                    MinutesPlayed = g.Sum(x => x.MinutesPlayed ?? 0),
                    YellowCards = g.Sum(x => x.YellowCards ?? 0),
                    RedCards = g.Sum(x => x.RedCards ?? 0),
                    AvgRating = g.Any(x => x.Rating != null)
                        ? Math.Round(g.Where(x => x.Rating != null).Average(x => (double)x.Rating!.Value), 2)
                        : (double?)null,
                    RecordedByName = recorderNames.TryGetValue(latestRecorderId, out var rn) ? rn : null,
                };
            })
            .OrderByDescending(d => d.Goals)
            .ToList();

        return Ok(new InternalPlayerStatsResponse { TeamId = teamId, Players = stats });
    }

    // ---------------------------------------------------------------------
    // Basketball box scores (PDF-imported; per-player + per-team).
    // These are the real basketball numbers (points, 3PT, rebounds, etc.),
    // distinct from the soccer-shaped /player-stats above.
    // ---------------------------------------------------------------------

    /// <summary>Per-player basketball box scores. Includes both per-game and cumulative rows
    /// (distinguished by <c>granularity</c>); team-total rows are excluded.</summary>
    [HttpGet("{teamId:guid}/match-player-stats")]
    public async Task<IActionResult> GetMatchPlayerStats(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var rows = await _db.PlayerMatchStats.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.RowType != "team_total")
            .Select(s => new
            {
                s.PlayerMatchStatsId,
                s.MatchStatsId,
                s.EventId,
                s.PlayerUserId,
                Name = s.Player.Name,
                OpponentName = s.MatchStats.OpponentName,
                Matchup = s.MatchStats.Matchup,
                GameNo = s.MatchStats.GameNo,
                Date = s.Event.StartAt,
                s.Granularity,
                s.Status,
                s.PlayerNo,
                s.IsStarter,
                s.IsCaptain,
                s.GamesPlayed,
                s.Starts,
                s.BbMinutes,
                s.TwoPtMA,
                s.ThreePtMA,
                s.FtMA,
                s.OffensiveRebounds,
                s.DefensiveRebounds,
                s.TotalRebounds,
                s.BbAssists,
                s.BbTurnovers,
                s.BbSteals,
                s.BbBlocks,
                s.BbPersonalFouls,
                s.BbFoulsDrawn,
                s.BbEfficiency,
                s.BbPoints,
            })
            .ToListAsync();

        var players = rows.Select(r => new InternalMatchPlayerStatDto
        {
            EventId = r.EventId,
            MatchStatsId = r.MatchStatsId,
            PlayerUserId = r.PlayerUserId,
            Name = r.Name,
            OpponentName = r.OpponentName,
            Matchup = r.Matchup,
            GameNo = r.GameNo,
            Date = r.Date,
            Granularity = r.Granularity,
            Status = r.Status,
            PlayerNo = r.PlayerNo,
            IsStarter = r.IsStarter,
            IsCaptain = r.IsCaptain,
            GamesPlayed = r.GamesPlayed,
            Starts = r.Starts,
            Minutes = r.BbMinutes,
            TwoPtMA = r.TwoPtMA,
            ThreePtMA = r.ThreePtMA,
            FtMA = r.FtMA,
            OffensiveRebounds = r.OffensiveRebounds,
            DefensiveRebounds = r.DefensiveRebounds,
            TotalRebounds = r.TotalRebounds,
            Assists = r.BbAssists,
            Turnovers = r.BbTurnovers,
            Steals = r.BbSteals,
            Blocks = r.BbBlocks,
            PersonalFouls = r.BbPersonalFouls,
            FoulsDrawn = r.BbFoulsDrawn,
            Efficiency = r.BbEfficiency,
            Points = r.BbPoints,
        }).ToList();

        return Ok(new InternalMatchPlayerStatsResponse { TeamId = teamId, Players = players });
    }

    /// <summary>Phase 1 unified box scores: the authoritative, deduplicated per-game
    /// per-player rows the chatbot consumes instead of merging its PDF-derived CSV with
    /// the DB. Same wire shape as match-player-stats, but: cumulative/team-total rows are
    /// dropped (only <c>game_player</c> granularity), and if the same game's PDF was
    /// imported more than once, the duplicate (player, opponent, game_no) rows collapse to
    /// the most recently imported one. This puts the PDF-vs-DB overlap resolution in SQL,
    /// once, for every consumer — the chatbot does no dedup of its own.</summary>
    [HttpGet("{teamId:guid}/unified-box-scores")]
    public async Task<IActionResult> GetUnifiedBoxScores(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var rows = await _db.PlayerMatchStats.AsNoTracking()
            .Where(s => s.TeamId == teamId
                && s.RowType != "team_total"
                && s.Granularity == "game_player")
            .Select(s => new
            {
                s.PlayerMatchStatsId,
                s.MatchStatsId,
                s.EventId,
                s.PlayerUserId,
                Name = s.Player.Name,
                OpponentName = s.MatchStats.OpponentName,
                Matchup = s.MatchStats.Matchup,
                GameNo = s.MatchStats.GameNo,
                Date = s.Event.StartAt,
                MatchCreatedAt = s.MatchStats.CreatedAt,
                s.Granularity,
                s.Status,
                s.PlayerNo,
                s.IsStarter,
                s.IsCaptain,
                s.GamesPlayed,
                s.Starts,
                s.BbMinutes,
                s.TwoPtMA,
                s.ThreePtMA,
                s.FtMA,
                s.OffensiveRebounds,
                s.DefensiveRebounds,
                s.TotalRebounds,
                s.BbAssists,
                s.BbTurnovers,
                s.BbSteals,
                s.BbBlocks,
                s.BbPersonalFouls,
                s.BbFoulsDrawn,
                s.BbEfficiency,
                s.BbPoints,
            })
            .ToListAsync();

        // Collapse re-imported duplicates: one row per (player, opponent, game_no),
        // keeping the most recently imported (latest MatchStats.CreatedAt).
        var deduped = rows
            .GroupBy(r => new
            {
                r.PlayerUserId,
                Opponent = (r.OpponentName ?? string.Empty).Trim().ToLowerInvariant(),
                Game = (r.GameNo ?? string.Empty).Trim().ToLowerInvariant(),
            })
            .Select(g => g.OrderByDescending(r => r.MatchCreatedAt).First());

        var players = deduped.Select(r => new InternalMatchPlayerStatDto
        {
            EventId = r.EventId,
            MatchStatsId = r.MatchStatsId,
            PlayerUserId = r.PlayerUserId,
            Name = r.Name,
            OpponentName = r.OpponentName,
            Matchup = r.Matchup,
            GameNo = r.GameNo,
            Date = r.Date,
            Granularity = r.Granularity,
            Status = r.Status,
            PlayerNo = r.PlayerNo,
            IsStarter = r.IsStarter,
            IsCaptain = r.IsCaptain,
            GamesPlayed = r.GamesPlayed,
            Starts = r.Starts,
            Minutes = r.BbMinutes,
            TwoPtMA = r.TwoPtMA,
            ThreePtMA = r.ThreePtMA,
            FtMA = r.FtMA,
            OffensiveRebounds = r.OffensiveRebounds,
            DefensiveRebounds = r.DefensiveRebounds,
            TotalRebounds = r.TotalRebounds,
            Assists = r.BbAssists,
            Turnovers = r.BbTurnovers,
            Steals = r.BbSteals,
            Blocks = r.BbBlocks,
            PersonalFouls = r.BbPersonalFouls,
            FoulsDrawn = r.BbFoulsDrawn,
            Efficiency = r.BbEfficiency,
            Points = r.BbPoints,
        }).ToList();

        return Ok(new InternalMatchPlayerStatsResponse { TeamId = teamId, Players = players });
    }

    /// <summary>Team identity for the chatbot's box-score team-code resolution (Phase 1).
    /// Returns the team name. A short box-score code (e.g. "EGY") is not stored on the team
    /// record — it lives only in the PDF-parsed box scores — so <c>code</c> is null here and
    /// the chatbot keeps its caller-supplied team code. Wiring this endpoint now lets a code
    /// be populated later (schema or derivation) without another Python change.</summary>
    [HttpGet("{teamId:guid}/team")]
    public async Task<IActionResult> GetTeam(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });

        var team = await _db.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.DeletedAt == null);
        if (team == null) return NotFound(new { error = "Team not found." });

        return Ok(new InternalTeamDto { TeamId = team.TeamId, Name = team.TeamName, Code = null });
    }

    /// <summary>Per-game and cumulative basketball team box scores (one row per game/total).</summary>
    [HttpGet("{teamId:guid}/match-team-stats")]
    public async Task<IActionResult> GetMatchTeamStats(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var games = await _db.MatchStats.AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new InternalMatchTeamStatDto
            {
                MatchStatsId = m.MatchStatsId,
                EventId = m.EventId,
                Category = m.Category,
                Granularity = m.Granularity,
                GameNo = m.GameNo,
                Matchup = m.Matchup,
                OpponentName = m.OpponentName,
                CompetitionName = m.CompetitionName,
                Venue = m.Venue,
                Result = m.Result,
                TeamScore = m.TeamScore,
                OpponentScore = m.OpponentScore,
                TwoPtMA = m.TwoPtMA,
                ThreePtMA = m.ThreePtMA,
                FtMA = m.FtMA,
                OffensiveRebounds = m.OffensiveRebounds,
                DefensiveRebounds = m.DefensiveRebounds,
                TotalRebounds = m.TotalRebounds,
                Assists = m.BbAssists,
                Turnovers = m.Turnovers,
                Steals = m.Steals,
                Blocks = m.Blocks,
                PersonalFouls = m.PersonalFouls,
                FoulsDrawn = m.FoulsDrawn,
                Efficiency = m.Efficiency,
                Points = m.Points,
                CreatedAt = m.CreatedAt,
            })
            .ToListAsync();

        return Ok(new InternalMatchTeamStatsResponse { TeamId = teamId, Games = games });
    }

    // ---------------------------------------------------------------------
    // Match analysis reports + per-lineup on/off splits (PDF-imported).
    // Each report carries a written summary plus its lineup combinations.
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/match-reports")]
    public async Task<IActionResult> GetMatchReports(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var reports = await _db.MatchAnalysisReports.AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .OrderByDescending(r => r.MatchDate)
            .Select(r => new InternalMatchReportDto
            {
                ReportId = r.ReportId,
                TeamCode = r.TeamCode,
                OpponentCode = r.OpponentCode,
                OpponentName = r.OpponentName,
                MatchDate = r.MatchDate,
                Competition = r.Competition,
                Venue = r.Venue,
                GameNo = r.GameNo,
                TeamScore = r.TeamScore,
                OpponentScore = r.OpponentScore,
                Result = r.Result,
                Summary = r.Summary,
                Lineups = r.Lineups
                    .OrderByDescending(l => l.TimeSeconds)
                    .Select(l => new InternalLineupAnalysisDto
                    {
                        LineupId = l.LineupId,
                        TeamCode = l.TeamCode,
                        LineupPlayers = l.LineupPlayers,
                        TimeOnCourt = l.TimeOnCourt,
                        TimeSeconds = l.TimeSeconds,
                        PointsFor = l.PointsFor,
                        PointsAgainst = l.PointsAgainst,
                        ScoreDiff = l.ScoreDiff,
                        PointsPerMinute = l.PointsPerMinute,
                        Rebounds = l.Rebounds,
                        Steals = l.Steals,
                        Turnovers = l.Turnovers,
                        Assists = l.Assists,
                    }).ToList(),
            })
            .ToListAsync();

        return Ok(new InternalMatchReportsResponse { TeamId = teamId, Reports = reports });
    }

    // ---------------------------------------------------------------------
    // Coaching context: tactical lineups (+ assigned players), coach notes,
    // and seasons. Read-only advisory context for the chatbot.
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/coaching-lineups")]
    public async Task<IActionResult> GetCoachingLineups(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var lineups = await _db.CoachingLineups.AsNoTracking()
            .Where(l => l.TeamId == teamId && l.DeletedAt == null)
            .OrderByDescending(l => l.UpdatedAt)
            .Select(l => new InternalCoachingLineupDto
            {
                LineupId = l.LineupId,
                Title = l.Title,
                Formation = l.Formation,
                GameModel = l.GameModel,
                TacticalNotes = l.TacticalNotes,
                Visibility = l.Visibility.ToString(),
                CreatedByName = l.Creator.Name,
                CreatedAt = l.CreatedAt,
                UpdatedAt = l.UpdatedAt,
                Players = l.Players
                    .OrderBy(p => p.SortOrder)
                    .Select(p => new InternalCoachingLineupPlayerDto
                    {
                        PlayerUserId = p.PlayerUserId,
                        Name = p.Player.Name,
                        Position = p.Position,
                        Unit = p.Unit,
                        SortOrder = p.SortOrder,
                        Instructions = p.Instructions,
                    }).ToList(),
            })
            .ToListAsync();

        return Ok(new InternalCoachingLineupsResponse { TeamId = teamId, Lineups = lineups });
    }

    [HttpGet("{teamId:guid}/coach-notes")]
    public async Task<IActionResult> GetCoachNotes(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var notes = await _db.CoachNotes.AsNoTracking()
            .Where(n => n.TeamId == teamId && n.DeletedAt == null)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new InternalCoachNoteDto
            {
                NoteId = n.NoteId,
                EventId = n.EventId,
                AuthorName = n.AuthorUser.Name,
                AuthorRole = n.AuthorRole,
                Body = n.Body,
                CreatedAt = n.CreatedAt,
                UpdatedAt = n.UpdatedAt,
            })
            .ToListAsync();

        return Ok(new InternalCoachNotesResponse { TeamId = teamId, Notes = notes });
    }

    [HttpGet("{teamId:guid}/seasons")]
    public async Task<IActionResult> GetSeasons(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var seasons = await _db.Seasons.AsNoTracking()
            .Where(s => s.TeamId == teamId)
            .OrderByDescending(s => s.StartDate)
            .Select(s => new InternalSeasonDto
            {
                SeasonId = s.SeasonId,
                Label = s.Label,
                StartDate = s.StartDate,
                EndDate = s.EndDate,
                IsCurrent = s.IsCurrent,
            })
            .ToListAsync();

        return Ok(new InternalSeasonsResponse { TeamId = teamId, Seasons = seasons });
    }

    // ---------------------------------------------------------------------
    // Coaching plans (read-only — advisory only, never written by the chatbot)
    // ---------------------------------------------------------------------

    [HttpGet("{teamId:guid}/plans")]
    public async Task<IActionResult> GetPlans(Guid teamId)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });
        if (!await TeamExistsAsync(teamId)) return NotFound(new { error = "Team not found." });

        var plans = await _db.CoachingPlans.AsNoTracking()
            .Where(p => p.TeamId == teamId && p.DeletedAt == null)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new InternalPlanDto
            {
                PlanId = p.PlanId,
                Title = p.Title,
                Description = p.Description,
                Content = p.Content,
                Visibility = p.Visibility.ToString(),
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
            })
            .ToListAsync();

        return Ok(new InternalPlansResponse { TeamId = teamId, Plans = plans });
    }

    // ---------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------

    private Task<bool> TeamExistsAsync(Guid teamId) =>
        _db.Teams.AsNoTracking().AnyAsync(t => t.TeamId == teamId && t.DeletedAt == null);

    /// <summary>Active team members joined to their player profile (PlayerId/position/jersey).</summary>
    private async Task<List<TeamPlayerRow>> LoadPlayersAsync(Guid teamId)
    {
        var members = await _db.TeamMemberships.AsNoTracking()
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == teamId && tm.Status == MembershipStatus.Active)
            .OrderBy(tm => tm.JoinedAt)
            .Select(tm => new { tm.UserId, tm.User.Name, Role = tm.Role.ToString() })
            .ToListAsync();

        var userIds = members.Select(m => m.UserId).ToList();
        var profiles = userIds.Count == 0
            ? new List<ProfileRow>()
            : await _db.PlayerProfiles.AsNoTracking()
                .Where(pp => userIds.Contains(pp.UserId) && pp.DeletedAt == null)
                .Select(pp => new ProfileRow(pp.UserId, pp.PlayerId, pp.Position, pp.JerseyNumber, pp.Height, pp.Weight))
                .ToListAsync();

        var profByUser = profiles.ToDictionary(p => p.UserId);

        return members.Select(m =>
        {
            profByUser.TryGetValue(m.UserId, out var p);
            return new TeamPlayerRow
            {
                UserId = m.UserId,
                Name = m.Name,
                Role = m.Role,
                PlayerId = p?.PlayerId,
                Position = p?.Position,
                JerseyNumber = p?.JerseyNumber,
                Height = p?.Height,
                Weight = p?.Weight,
            };
        }).ToList();
    }

    /// <summary>Per-player uncleared-injury count + most recent injury type.</summary>
    private async Task<Dictionary<Guid, (int Count, string? LatestType)>> LoadUnclearedInjuryCountsAsync(Guid teamId)
    {
        var records = await _db.MedicalRecords.AsNoTracking()
            .Where(mr => mr.TeamId == teamId && !mr.IsCleared)
            .OrderByDescending(mr => mr.RecordDate)
            .Select(mr => new { mr.PlayerId, mr.InjuryType })
            .ToListAsync();

        return records
            .GroupBy(mr => mr.PlayerId)
            .ToDictionary(g => g.Key, g => (g.Count(), g.First().InjuryType));
    }

    /// <summary>Resolve a set of user ids to display names in one query (recorder/author lookup).</summary>
    private async Task<Dictionary<Guid, string>> LoadUserNamesAsync(IEnumerable<Guid> userIds)
    {
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.Name);
    }

    private bool IsServiceTokenValid()
    {
        var expected = _config["Microservice:ServiceToken"];
        if (string.IsNullOrWhiteSpace(expected)) return false; // fail closed if unset

        string? provided = null;
        var auth = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            provided = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(provided))
            provided = Request.Headers["X-Service-Token"].ToString();

        if (string.IsNullOrEmpty(provided)) return false;

        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed class TeamPlayerRow
    {
        public Guid UserId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public Guid? PlayerId { get; init; }
        public string? Position { get; init; }
        public int? JerseyNumber { get; init; }
        public decimal? Height { get; init; }
        public decimal? Weight { get; init; }
    }

    private sealed record ProfileRow(Guid UserId, Guid PlayerId, string? Position, int? JerseyNumber, decimal? Height, decimal? Weight);
}

// =========================================================================
// Wire DTOs (snake_case to match the microservice)
// =========================================================================

public sealed class InternalTeamDto
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string? Code { get; set; }
}

public sealed class InternalRosterResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("members")] public List<InternalRosterMemberDto> Members { get; set; } = new();
}

public sealed class InternalRosterMemberDto
{
    [JsonPropertyName("user_id")] public Guid UserId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("position")] public string? Position { get; set; }
    [JsonPropertyName("jersey_number")] public int? JerseyNumber { get; set; }
    [JsonPropertyName("height")] public decimal? Height { get; set; }
    [JsonPropertyName("weight")] public decimal? Weight { get; set; }
    [JsonPropertyName("is_injured")] public bool IsInjured { get; set; }
    [JsonPropertyName("injury_type")] public string? InjuryType { get; set; }
    [JsonPropertyName("injury_count")] public int InjuryCount { get; set; }
}

public sealed class InternalInjuriesResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("injuries")] public List<InternalInjuryDto> Injuries { get; set; } = new();
}

public sealed class InternalInjuryDto
{
    [JsonPropertyName("player_id")] public Guid PlayerId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("injury_type")] public string? InjuryType { get; set; }
    [JsonPropertyName("diagnosis")] public string? Diagnosis { get; set; }
    [JsonPropertyName("record_date")] public DateTime RecordDate { get; set; }
    [JsonPropertyName("expected_return_date")] public DateOnly? ExpectedReturnDate { get; set; }
    [JsonPropertyName("recovery_tips")] public string? RecoveryTips { get; set; }
    [JsonPropertyName("recorded_by_name")] public string? RecordedByName { get; set; }
}

public sealed class InternalAvailabilityResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("players")] public List<InternalAvailabilityDto> Players { get; set; } = new();
}

public sealed class InternalAvailabilityDto
{
    [JsonPropertyName("user_id")] public Guid UserId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("position")] public string? Position { get; set; }
    [JsonPropertyName("jersey_number")] public int? JerseyNumber { get; set; }
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class InternalScheduleResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("events")] public List<InternalScheduleEventDto> Events { get; set; } = new();
}

public sealed class InternalScheduleEventDto
{
    [JsonPropertyName("event_id")] public Guid EventId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("event_type")] public string EventType { get; set; } = string.Empty;
    [JsonPropertyName("start_at")] public DateTime StartAt { get; set; }
    [JsonPropertyName("end_at")] public DateTime? EndAt { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
}

public sealed class InternalAttendanceResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("window_days")] public int WindowDays { get; set; }
    [JsonPropertyName("players")] public List<InternalAttendanceDto> Players { get; set; } = new();
}

public sealed class InternalAttendanceDto
{
    [JsonPropertyName("player_id")] public Guid PlayerId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("present")] public int Present { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("rate")] public double Rate { get; set; }
    [JsonPropertyName("recorded_by_name")] public string? RecordedByName { get; set; }
}

public sealed class InternalFitnessResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("players")] public List<InternalFitnessDto> Players { get; set; } = new();
}

public sealed class InternalFitnessDto
{
    [JsonPropertyName("player_id")] public Guid PlayerId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("test_date")] public DateTime TestDate { get; set; }
    [JsonPropertyName("height")] public decimal? Height { get; set; }
    [JsonPropertyName("weight")] public decimal? Weight { get; set; }
    [JsonPropertyName("bmi")] public decimal? Bmi { get; set; }
    [JsonPropertyName("body_fat_pct")] public decimal? BodyFatPct { get; set; }
    [JsonPropertyName("speed_test_result")] public decimal? SpeedTestResult { get; set; }
    [JsonPropertyName("endurance_score")] public decimal? EnduranceScore { get; set; }
    [JsonPropertyName("custom_test_name")] public string? CustomTestName { get; set; }
    [JsonPropertyName("custom_test_result")] public decimal? CustomTestResult { get; set; }
    [JsonPropertyName("recorded_by_name")] public string? RecordedByName { get; set; }
}

public sealed class InternalPlayerStatsResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("players")] public List<InternalPlayerStatDto> Players { get; set; } = new();
}

public sealed class InternalPlayerStatDto
{
    [JsonPropertyName("user_id")] public Guid UserId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("matches")] public int Matches { get; set; }
    [JsonPropertyName("goals")] public int Goals { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
    [JsonPropertyName("minutes_played")] public int MinutesPlayed { get; set; }
    [JsonPropertyName("yellow_cards")] public int YellowCards { get; set; }
    [JsonPropertyName("red_cards")] public int RedCards { get; set; }
    [JsonPropertyName("avg_rating")] public double? AvgRating { get; set; }
    [JsonPropertyName("recorded_by_name")] public string? RecordedByName { get; set; }
}

public sealed class InternalMatchPlayerStatsResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("players")] public List<InternalMatchPlayerStatDto> Players { get; set; } = new();
}

public sealed class InternalMatchPlayerStatDto
{
    [JsonPropertyName("event_id")] public Guid EventId { get; set; }
    [JsonPropertyName("match_stats_id")] public Guid MatchStatsId { get; set; }
    [JsonPropertyName("player_user_id")] public Guid PlayerUserId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("opponent_name")] public string? OpponentName { get; set; }
    [JsonPropertyName("matchup")] public string? Matchup { get; set; }
    [JsonPropertyName("game_no")] public string? GameNo { get; set; }
    [JsonPropertyName("date")] public DateTime? Date { get; set; }
    [JsonPropertyName("granularity")] public string? Granularity { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("player_no")] public int? PlayerNo { get; set; }
    [JsonPropertyName("is_starter")] public bool? IsStarter { get; set; }
    [JsonPropertyName("is_captain")] public bool? IsCaptain { get; set; }
    [JsonPropertyName("games_played")] public int? GamesPlayed { get; set; }
    [JsonPropertyName("starts")] public int? Starts { get; set; }
    [JsonPropertyName("minutes")] public string? Minutes { get; set; }
    [JsonPropertyName("two_pt_ma")] public string? TwoPtMA { get; set; }
    [JsonPropertyName("three_pt_ma")] public string? ThreePtMA { get; set; }
    [JsonPropertyName("ft_ma")] public string? FtMA { get; set; }
    [JsonPropertyName("offensive_rebounds")] public int? OffensiveRebounds { get; set; }
    [JsonPropertyName("defensive_rebounds")] public int? DefensiveRebounds { get; set; }
    [JsonPropertyName("total_rebounds")] public int? TotalRebounds { get; set; }
    [JsonPropertyName("assists")] public int? Assists { get; set; }
    [JsonPropertyName("turnovers")] public int? Turnovers { get; set; }
    [JsonPropertyName("steals")] public int? Steals { get; set; }
    [JsonPropertyName("blocks")] public int? Blocks { get; set; }
    [JsonPropertyName("personal_fouls")] public int? PersonalFouls { get; set; }
    [JsonPropertyName("fouls_drawn")] public int? FoulsDrawn { get; set; }
    [JsonPropertyName("efficiency")] public int? Efficiency { get; set; }
    [JsonPropertyName("points")] public int? Points { get; set; }
}

public sealed class InternalMatchTeamStatsResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("games")] public List<InternalMatchTeamStatDto> Games { get; set; } = new();
}

public sealed class InternalMatchTeamStatDto
{
    [JsonPropertyName("match_stats_id")] public Guid MatchStatsId { get; set; }
    [JsonPropertyName("event_id")] public Guid EventId { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("granularity")] public string? Granularity { get; set; }
    [JsonPropertyName("game_no")] public string? GameNo { get; set; }
    [JsonPropertyName("matchup")] public string? Matchup { get; set; }
    [JsonPropertyName("opponent_name")] public string? OpponentName { get; set; }
    [JsonPropertyName("competition_name")] public string? CompetitionName { get; set; }
    [JsonPropertyName("venue")] public string? Venue { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("team_score")] public int? TeamScore { get; set; }
    [JsonPropertyName("opponent_score")] public int? OpponentScore { get; set; }
    [JsonPropertyName("two_pt_ma")] public string? TwoPtMA { get; set; }
    [JsonPropertyName("three_pt_ma")] public string? ThreePtMA { get; set; }
    [JsonPropertyName("ft_ma")] public string? FtMA { get; set; }
    [JsonPropertyName("offensive_rebounds")] public int? OffensiveRebounds { get; set; }
    [JsonPropertyName("defensive_rebounds")] public int? DefensiveRebounds { get; set; }
    [JsonPropertyName("total_rebounds")] public int? TotalRebounds { get; set; }
    [JsonPropertyName("assists")] public int? Assists { get; set; }
    [JsonPropertyName("turnovers")] public int? Turnovers { get; set; }
    [JsonPropertyName("steals")] public int? Steals { get; set; }
    [JsonPropertyName("blocks")] public int? Blocks { get; set; }
    [JsonPropertyName("personal_fouls")] public int? PersonalFouls { get; set; }
    [JsonPropertyName("fouls_drawn")] public int? FoulsDrawn { get; set; }
    [JsonPropertyName("efficiency")] public int? Efficiency { get; set; }
    [JsonPropertyName("points")] public int? Points { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

public sealed class InternalMatchReportsResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("reports")] public List<InternalMatchReportDto> Reports { get; set; } = new();
}

public sealed class InternalMatchReportDto
{
    [JsonPropertyName("report_id")] public Guid ReportId { get; set; }
    [JsonPropertyName("team_code")] public string? TeamCode { get; set; }
    [JsonPropertyName("opponent_code")] public string? OpponentCode { get; set; }
    [JsonPropertyName("opponent_name")] public string? OpponentName { get; set; }
    [JsonPropertyName("match_date")] public DateOnly MatchDate { get; set; }
    [JsonPropertyName("competition")] public string? Competition { get; set; }
    [JsonPropertyName("venue")] public string? Venue { get; set; }
    [JsonPropertyName("game_no")] public string? GameNo { get; set; }
    [JsonPropertyName("team_score")] public int TeamScore { get; set; }
    [JsonPropertyName("opponent_score")] public int OpponentScore { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("lineups")] public List<InternalLineupAnalysisDto> Lineups { get; set; } = new();
}

public sealed class InternalLineupAnalysisDto
{
    [JsonPropertyName("lineup_id")] public Guid LineupId { get; set; }
    [JsonPropertyName("team_code")] public string? TeamCode { get; set; }
    [JsonPropertyName("lineup_players")] public string? LineupPlayers { get; set; }
    [JsonPropertyName("time_on_court")] public string? TimeOnCourt { get; set; }
    [JsonPropertyName("time_seconds")] public int TimeSeconds { get; set; }
    [JsonPropertyName("points_for")] public int PointsFor { get; set; }
    [JsonPropertyName("points_against")] public int PointsAgainst { get; set; }
    [JsonPropertyName("score_diff")] public int ScoreDiff { get; set; }
    [JsonPropertyName("points_per_minute")] public decimal PointsPerMinute { get; set; }
    [JsonPropertyName("rebounds")] public int Rebounds { get; set; }
    [JsonPropertyName("steals")] public int Steals { get; set; }
    [JsonPropertyName("turnovers")] public int Turnovers { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
}

public sealed class InternalCoachingLineupsResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("lineups")] public List<InternalCoachingLineupDto> Lineups { get; set; } = new();
}

public sealed class InternalCoachingLineupDto
{
    [JsonPropertyName("lineup_id")] public Guid LineupId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("formation")] public string? Formation { get; set; }
    [JsonPropertyName("game_model")] public string? GameModel { get; set; }
    [JsonPropertyName("tactical_notes")] public string? TacticalNotes { get; set; }
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = string.Empty;
    [JsonPropertyName("created_by_name")] public string? CreatedByName { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("players")] public List<InternalCoachingLineupPlayerDto> Players { get; set; } = new();
}

public sealed class InternalCoachingLineupPlayerDto
{
    [JsonPropertyName("player_user_id")] public Guid PlayerUserId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("position")] public string? Position { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
}

public sealed class InternalCoachNotesResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("notes")] public List<InternalCoachNoteDto> Notes { get; set; } = new();
}

public sealed class InternalCoachNoteDto
{
    [JsonPropertyName("note_id")] public Guid NoteId { get; set; }
    [JsonPropertyName("event_id")] public Guid EventId { get; set; }
    [JsonPropertyName("author_name")] public string? AuthorName { get; set; }
    [JsonPropertyName("author_role")] public string? AuthorRole { get; set; }
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public sealed class InternalSeasonsResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("seasons")] public List<InternalSeasonDto> Seasons { get; set; } = new();
}

public sealed class InternalSeasonDto
{
    [JsonPropertyName("season_id")] public Guid SeasonId { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("start_date")] public DateOnly StartDate { get; set; }
    [JsonPropertyName("end_date")] public DateOnly EndDate { get; set; }
    [JsonPropertyName("is_current")] public bool IsCurrent { get; set; }
}

public sealed class InternalPlansResponse
{
    [JsonPropertyName("team_id")] public Guid TeamId { get; set; }
    [JsonPropertyName("plans")] public List<InternalPlanDto> Plans { get; set; } = new();
}

public sealed class InternalPlanDto
{
    [JsonPropertyName("plan_id")] public Guid PlanId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}
