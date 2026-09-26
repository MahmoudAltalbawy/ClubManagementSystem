using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly AppDbContext _db;

    public SearchService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<SearchResponseDto> SearchAsync(
        Guid callerUserId,
        string query,
        string type,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var q = (query ?? string.Empty).Trim();
        var typeKey = string.IsNullOrWhiteSpace(type) ? "all" : type.Trim().ToLowerInvariant();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        if (q.Length < 2)
            return new SearchResponseDto { Query = q, Type = typeKey };

        var visibleTeamIds = await GetVisibleTeamIdsAsync(callerUserId, cancellationToken);
        var callerRoles = await _db.TeamMemberships
            .Where(tm => tm.UserId == callerUserId && visibleTeamIds.Contains(tm.TeamId) && tm.Status == MembershipStatus.Active)
            .Select(tm => new { tm.TeamId, tm.Role })
            .ToListAsync(cancellationToken);
        var roleByTeam = callerRoles.ToDictionary(x => x.TeamId, x => x.Role);
        var isAdmin = await _db.Users.AnyAsync(u => u.UserId == callerUserId && u.IsAdmin, cancellationToken);

        var results = new List<SearchResultDto>();
        if (Matches(typeKey, "teams")) results.AddRange(await SearchTeamsAsync(visibleTeamIds, q, cancellationToken));
        if (Matches(typeKey, "users")) results.AddRange(await SearchUsersAsync(visibleTeamIds, callerUserId, q, cancellationToken));
        if (Matches(typeKey, "events")) results.AddRange(await SearchEventsAsync(visibleTeamIds, q, cancellationToken));
        if (Matches(typeKey, "plans")) results.AddRange(await SearchPlansAsync(visibleTeamIds, callerUserId, roleByTeam, isAdmin, q, cancellationToken));
        if (Matches(typeKey, "announcements")) results.AddRange(await SearchAnnouncementsAsync(visibleTeamIds, q, cancellationToken));
        if (Matches(typeKey, "stats")) results.AddRange(await SearchStatsAsync(visibleTeamIds, callerUserId, roleByTeam, isAdmin, q, cancellationToken));

        var ordered = results
            .OrderByDescending(r => r.OccurredAt ?? DateTime.MinValue)
            .ThenBy(r => r.Type)
            .ThenBy(r => r.Title)
            .ToList();

        return new SearchResponseDto
        {
            Query = q,
            Type = typeKey,
            TotalCount = ordered.Count,
            Results = ordered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList()
        };
    }

    private async Task<List<Guid>> GetVisibleTeamIdsAsync(Guid callerUserId, CancellationToken cancellationToken)
    {
        if (await _db.Users.AnyAsync(u => u.UserId == callerUserId && u.IsAdmin, cancellationToken))
            return await _db.Teams.Select(t => t.TeamId).ToListAsync(cancellationToken);

        var teamMemberships = _db.TeamMemberships
            .Where(tm => tm.UserId == callerUserId && tm.Status == MembershipStatus.Active)
            .Select(tm => tm.TeamId);

        var clubIds = _db.ClubMemberships
            .Where(cm => cm.UserId == callerUserId && cm.Status == MembershipStatus.Active)
            .Select(cm => cm.ClubId)
            .Union(_db.Clubs.Where(c => c.CreatedBy == callerUserId).Select(c => c.ClubId));

        var clubTeams = _db.Teams
            .Where(t => t.ClubId.HasValue && clubIds.Contains(t.ClubId.Value))
            .Select(t => t.TeamId);

        return await teamMemberships
            .Union(clubTeams)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private Task<List<SearchResultDto>> SearchTeamsAsync(List<Guid> visibleTeamIds, string q, CancellationToken cancellationToken) =>
        _db.Teams
            .Include(t => t.Club)
            .Where(t => visibleTeamIds.Contains(t.TeamId) && EF.Functions.ILike(t.TeamName, $"%{q}%"))
            .Select(t => new SearchResultDto
            {
                Id = t.TeamId,
                Type = "team",
                Title = t.TeamName,
                Subtitle = t.Club == null ? null : t.Club.Name,
                ClubId = t.ClubId,
                TeamId = t.TeamId,
                TargetId = t.TeamId,
                TargetRoute = $"/teams/{t.TeamId}",
                ImageUrl = t.ImageUrl,
                OccurredAt = t.UpdatedAt
            })
            .Take(30)
            .ToListAsync(cancellationToken);

    private Task<List<SearchResultDto>> SearchUsersAsync(List<Guid> visibleTeamIds, Guid callerUserId, string q, CancellationToken cancellationToken) =>
        _db.TeamMemberships
            .Include(tm => tm.User)
            .Include(tm => tm.Team)
            .Where(tm => visibleTeamIds.Contains(tm.TeamId) &&
                tm.Status == MembershipStatus.Active &&
                tm.UserId != callerUserId &&
                (EF.Functions.ILike(tm.User.Name, $"%{q}%") ||
                 EF.Functions.ILike(tm.User.Email, $"%{q}%")))
            .GroupBy(tm => new { tm.UserId, tm.User.Name, tm.User.Email, tm.User.ProfileImageUrl })
            .Select(g => new SearchResultDto
            {
                Id = g.Key.UserId,
                Type = "user",
                Title = g.Key.Name,
                Subtitle = g.Key.Email,
                TeamId = g.Select(x => x.TeamId).FirstOrDefault(),
                TargetId = g.Key.UserId,
                TargetRoute = $"/users/{g.Key.UserId}",
                ImageUrl = g.Key.ProfileImageUrl,
                OccurredAt = g.Max(x => x.UpdatedAt)
            })
            .Take(30)
            .ToListAsync(cancellationToken);

    private Task<List<SearchResultDto>> SearchEventsAsync(List<Guid> visibleTeamIds, string q, CancellationToken cancellationToken) =>
        _db.Events
            .Include(e => e.Team)
            .Where(e => visibleTeamIds.Contains(e.TeamId) &&
                (EF.Functions.ILike(e.Title, $"%{q}%") ||
                 (e.Description != null && EF.Functions.ILike(e.Description, $"%{q}%")) ||
                 (e.Location != null && EF.Functions.ILike(e.Location, $"%{q}%"))))
            .Select(e => new SearchResultDto
            {
                Id = e.EventId,
                Type = "event",
                Title = e.Title,
                Subtitle = e.Team.TeamName,
                TeamId = e.TeamId,
                TargetId = e.EventId,
                TargetRoute = $"/teams/{e.TeamId}/events/{e.EventId}",
                OccurredAt = e.StartAt
            })
            .Take(30)
            .ToListAsync(cancellationToken);

    private async Task<List<SearchResultDto>> SearchPlansAsync(
        List<Guid> visibleTeamIds,
        Guid callerUserId,
        Dictionary<Guid, RoleNameType> roleByTeam,
        bool isAdmin,
        string q,
        CancellationToken cancellationToken)
    {
        var plans = await _db.CoachingPlans
            .Include(p => p.Team)
            .Include(p => p.Creator)
            .Where(p => visibleTeamIds.Contains(p.TeamId) &&
                (EF.Functions.ILike(p.Title, $"%{q}%") ||
                 (p.Description != null && EF.Functions.ILike(p.Description, $"%{q}%"))))
            .Take(50)
            .ToListAsync(cancellationToken);

        return plans
            .Where(p => CanViewPlanLike(p.TeamId, p.CreatedBy, p.Visibility, callerUserId, roleByTeam, isAdmin))
            .Select(p => new SearchResultDto
            {
                Id = p.PlanId,
                Type = "plan",
                Title = p.Title,
                Subtitle = p.Team.TeamName,
                TeamId = p.TeamId,
                TargetId = p.PlanId,
                TargetRoute = $"/teams/{p.TeamId}/plans/{p.PlanId}",
                OccurredAt = p.UpdatedAt
            })
            .ToList();
    }

    private Task<List<SearchResultDto>> SearchAnnouncementsAsync(List<Guid> visibleTeamIds, string q, CancellationToken cancellationToken) =>
        _db.Announcements
            .Include(a => a.Team)
            .Where(a => visibleTeamIds.Contains(a.TeamId) &&
                (EF.Functions.ILike(a.Title, $"%{q}%") ||
                 EF.Functions.ILike(a.Content, $"%{q}%")))
            .Select(a => new SearchResultDto
            {
                Id = a.AnnouncementId,
                Type = "announcement",
                Title = a.Title,
                Subtitle = a.Team.TeamName,
                TeamId = a.TeamId,
                TargetId = a.AnnouncementId,
                TargetRoute = $"/teams/{a.TeamId}/announcements/{a.AnnouncementId}",
                ImageUrl = a.ImageUrl,
                OccurredAt = a.CreatedAt
            })
            .Take(30)
            .ToListAsync(cancellationToken);

    private async Task<List<SearchResultDto>> SearchStatsAsync(
        List<Guid> visibleTeamIds,
        Guid callerUserId,
        Dictionary<Guid, RoleNameType> roleByTeam,
        bool isAdmin,
        string q,
        CancellationToken cancellationToken)
    {
        var stats = await _db.MatchStats
            .Include(s => s.Event)
            .Include(s => s.Team)
            .Where(s => visibleTeamIds.Contains(s.TeamId) &&
                (s.OpponentName != null && EF.Functions.ILike(s.OpponentName, $"%{q}%") ||
                 s.Result != null && EF.Functions.ILike(s.Result, $"%{q}%") ||
                 s.CompetitionName != null && EF.Functions.ILike(s.CompetitionName, $"%{q}%") ||
                 s.Matchup != null && EF.Functions.ILike(s.Matchup, $"%{q}%") ||
                 EF.Functions.ILike(s.Event.Title, $"%{q}%")))
            .Take(30)
            .ToListAsync(cancellationToken);

        return stats
            .Where(s => CanViewTeamStats(s.TeamId, callerUserId, roleByTeam, isAdmin))
            .Select(s => new SearchResultDto
            {
                Id = s.MatchStatsId,
                Type = "stats",
                Title = s.Event.Title,
                Subtitle = s.OpponentName ?? s.Result ?? s.Team.TeamName,
                TeamId = s.TeamId,
                TargetId = s.EventId,
                TargetRoute = $"/teams/{s.TeamId}/stats/{s.EventId}",
                OccurredAt = s.UpdatedAt
            })
            .ToList();
    }

    private static bool Matches(string requested, string candidate) =>
        requested == "all" || requested == candidate;

    private static bool CanViewPlanLike(
        Guid teamId,
        Guid createdBy,
        PlanVisibility visibility,
        Guid callerUserId,
        Dictionary<Guid, RoleNameType> roleByTeam,
        bool isAdmin)
    {
        if (isAdmin) return true;
        roleByTeam.TryGetValue(teamId, out var role);
        if (role == RoleNameType.Coach && createdBy == callerUserId) return true;
        return visibility is PlanVisibility.TeamVisible or PlanVisibility.PlayerAssigned;
    }

    private static bool CanViewTeamStats(
        Guid teamId,
        Guid callerUserId,
        Dictionary<Guid, RoleNameType> roleByTeam,
        bool isAdmin)
    {
        if (isAdmin) return true;
        if (!roleByTeam.TryGetValue(teamId, out var role)) return false;
        return role != RoleNameType.Player || callerUserId != Guid.Empty;
    }
}
