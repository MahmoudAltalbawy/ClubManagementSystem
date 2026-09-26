using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class MatchAnalysisService : IMatchAnalysisService
{
    private readonly AppDbContext _db;

    public MatchAnalysisService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<MatchAnalysisReportDto>> GetReportsAsync(Guid callerUserId)
    {
        await EnsureCanViewAnalysisAsync(callerUserId);

        var reports = await _db.MatchAnalysisReports
            .Include(r => r.Lineups)
            .Include(r => r.Documents)
            .OrderByDescending(r => r.MatchDate)
            .ToListAsync();

        return reports.Select(ToReportDto).ToList();
    }

    public async Task<MatchAnalysisReportDto> GetReportAsync(Guid reportId, Guid callerUserId)
    {
        await EnsureCanViewAnalysisAsync(callerUserId);

        var report = await _db.MatchAnalysisReports
            .Include(r => r.Lineups)
            .Include(r => r.Documents)
            .FirstOrDefaultAsync(r => r.ReportId == reportId)
            ?? throw new InvalidOperationException("Match analysis report not found.");

        return ToReportDto(report);
    }

    public async Task<MatchAnalysisSummaryDto> GetSummaryAsync(Guid callerUserId)
    {
        await EnsureCanViewAnalysisAsync(callerUserId);

        var reports = await _db.MatchAnalysisReports.ToListAsync();
        var lineups = await _db.MatchLineupAnalyses
            .OrderByDescending(l => l.ScoreDiff)
            .ThenByDescending(l => l.PointsPerMinute)
            .ThenByDescending(l => l.TimeSeconds)
            .Take(6)
            .ToListAsync();

        return new MatchAnalysisSummaryDto
        {
            TotalMatches = reports.Count,
            Wins = reports.Count(r => r.Result == "Win"),
            Losses = reports.Count(r => r.Result == "Loss"),
            AverageScoreDiff = reports.Count == 0 ? 0 : reports.Average(r => (decimal)(r.TeamScore - r.OpponentScore)),
            AveragePointsFor = reports.Count == 0 ? 0 : reports.Average(r => (decimal)r.TeamScore),
            AveragePointsAgainst = reports.Count == 0 ? 0 : reports.Average(r => (decimal)r.OpponentScore),
            BestLineups = lineups.Select(ToLineupDto).ToList(),
        };
    }

    private async Task EnsureCanViewAnalysisAsync(Guid userId)
    {
        if (await _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin)) return;

        var hasTeamRole = await _db.TeamMemberships.AnyAsync(tm =>
            tm.UserId == userId &&
            tm.Status == MembershipStatus.Active &&
            (tm.Role == RoleNameType.Coach ||
             tm.Role == RoleNameType.TeamAnalyst ||
             tm.Role == RoleNameType.TeamManager));

        if (hasTeamRole) return;

        var hasClubAccess = await _db.ClubMemberships.AnyAsync(cm =>
            cm.UserId == userId &&
            cm.Status == MembershipStatus.Active &&
            (cm.Role == RoleNameType.ClubManager || cm.Role == RoleNameType.TeamManager)) ||
            await _db.Clubs.AnyAsync(c => c.CreatedBy == userId && c.DeletedAt == null);

        if (!hasClubAccess)
            throw new UnauthorizedAccessException("Only coaches, analysts, and managers can view match analysis.");
    }

    private static MatchAnalysisReportDto ToReportDto(MatchAnalysisReport report)
    {
        return new MatchAnalysisReportDto
        {
            ReportId = report.ReportId,
            TeamId = report.TeamId,
            TeamCode = report.TeamCode,
            OpponentCode = report.OpponentCode,
            OpponentName = report.OpponentName,
            MatchDate = report.MatchDate,
            Competition = report.Competition,
            Venue = report.Venue,
            GameNo = report.GameNo,
            TeamScore = report.TeamScore,
            OpponentScore = report.OpponentScore,
            Result = report.Result,
            Summary = report.Summary,
            TopLineups = report.Lineups
                .OrderByDescending(l => l.ScoreDiff)
                .ThenByDescending(l => l.PointsPerMinute)
                .ThenByDescending(l => l.TimeSeconds)
                .Select(ToLineupDto)
                .ToList(),
            Documents = report.Documents
                .OrderBy(d => d.DocumentType)
                .Select(d => new MatchAnalysisDocumentDto
                {
                    DocumentId = d.DocumentId,
                    ReportId = d.ReportId,
                    DocumentType = d.DocumentType,
                    FileName = d.FileName,
                    FileUrl = $"/uploads/analysis-documents/{Uri.EscapeDataString(d.FileName)}",
                })
                .ToList(),
        };
    }

    private static MatchLineupAnalysisDto ToLineupDto(MatchLineupAnalysis lineup)
    {
        var players = lineup.LineupPlayers
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new MatchLineupAnalysisDto
        {
            LineupId = lineup.LineupId,
            ReportId = lineup.ReportId,
            TeamCode = lineup.TeamCode,
            LineupPlayers = lineup.LineupPlayers,
            Players = players,
            TimeOnCourt = lineup.TimeOnCourt,
            TimeSeconds = lineup.TimeSeconds,
            PointsFor = lineup.PointsFor,
            PointsAgainst = lineup.PointsAgainst,
            ScoreDiff = lineup.ScoreDiff,
            PointsPerMinute = lineup.PointsPerMinute,
            Rebounds = lineup.Rebounds,
            Steals = lineup.Steals,
            Turnovers = lineup.Turnovers,
            Assists = lineup.Assists,
        };
    }
}
