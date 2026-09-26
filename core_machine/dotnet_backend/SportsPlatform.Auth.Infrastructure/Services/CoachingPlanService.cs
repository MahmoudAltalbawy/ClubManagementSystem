using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class CoachingPlanService : ICoachingPlanService
{
    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;

    public CoachingPlanService(AppDbContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public async Task<PlanDto> CreatePlanAsync(Guid clubId, Guid teamId, Guid callerUserId, CreatePlanRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanCreatePlanAsync(team, callerUserId);

        var now = DateTime.UtcNow;
        var entity = new CoachingPlan
        {
            PlanId = Guid.NewGuid(),
            TeamId = team.TeamId,
            CreatedBy = callerUserId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            Content = request.Content.Trim(),
            Visibility = ParseVisibility(request.Visibility, PlanVisibility.Draft),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.CoachingPlans.Add(entity);
        await _db.SaveChangesAsync();

        await NotifyVisiblePlanAsync(team, callerUserId, entity.PlanId, entity.Title, entity.Visibility, "PlanCreated", "New coaching plan");

        return await BuildDtoAsync(entity.PlanId);
    }

    public async Task<List<PlanDto>> GetTeamPlansAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var callerRole = await GetCallerTeamRoleAsync(team.TeamId, callerUserId);
        var isAdmin = await IsAdminAsync(callerUserId);

        var query = _db.CoachingPlans
            .Include(p => p.Creator)
            .Where(p => p.TeamId == teamId);

        if (!isAdmin)
        {
            query = query.Where(p =>
                (callerRole == RoleNameType.Coach && p.CreatedBy == callerUserId) ||
                p.Visibility == PlanVisibility.TeamVisible ||
                p.Visibility == PlanVisibility.PlayerAssigned);
        }

        return await query
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new PlanDto
            {
                PlanId = p.PlanId,
                TeamId = p.TeamId,
                Title = p.Title,
                Description = p.Description,
                Content = p.Content,
                Visibility = p.Visibility.ToString(),
                CreatorName = p.Creator.Name,
                CreatedBy = p.CreatedBy,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                Documents = p.Documents
                    .Where(d => d.DeletedAt == null)
                    .OrderByDescending(d => d.CreatedAt)
                    .Select(d => new PlanDocumentDto
                    {
                        DocumentId = d.DocumentId,
                        FileName = d.OriginalFileName,
                        ContentType = d.ContentType,
                        FileSizeBytes = d.FileSize,
                        UploadedAt = d.CreatedAt
                    })
                    .ToList()
            })
            .ToListAsync();
    }

    public async Task<PlanDto> GetPlanAsync(Guid clubId, Guid teamId, Guid planId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var plan = await _db.CoachingPlans
            .FirstOrDefaultAsync(p => p.PlanId == planId && p.TeamId == teamId)
            ?? throw new InvalidOperationException("Plan not found.");

        await EnsureCanViewPlanAsync(team, plan, callerUserId);

        return await BuildDtoAsync(planId);
    }

    public async Task<PlanDto> UpdatePlanAsync(Guid clubId, Guid teamId, Guid planId, Guid callerUserId, UpdatePlanRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        var plan = await _db.CoachingPlans
            .FirstOrDefaultAsync(p => p.PlanId == planId && p.TeamId == teamId)
            ?? throw new InvalidOperationException("Plan not found.");

        await EnsureCanEditPlanAsync(team, plan, callerUserId);

        plan.Title = request.Title.Trim();
        plan.Description = request.Description?.Trim();
        plan.Content = request.Content.Trim();
        plan.Visibility = ParseVisibility(request.Visibility, plan.Visibility);
        plan.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await NotifyVisiblePlanAsync(team, callerUserId, plan.PlanId, plan.Title, plan.Visibility, "PlanUpdated", "Coaching plan updated");
        return await BuildDtoAsync(planId);
    }

    public async Task DeletePlanAsync(Guid clubId, Guid teamId, Guid planId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        var plan = await _db.CoachingPlans
            .FirstOrDefaultAsync(p => p.PlanId == planId && p.TeamId == teamId)
            ?? throw new InvalidOperationException("Plan not found.");

        await EnsureCanEditPlanAsync(team, plan, callerUserId);

        plan.DeletedAt = DateTime.UtcNow;
        plan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<LineupDto> CreateLineupAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateLineupRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanCreatePlanAsync(team, callerUserId);
        await EnsureValidLineupPlayersAsync(teamId, request.Players);

        var linkedEvent = await GetLineupEventAsync(teamId, request.EventId);
        var now = DateTime.UtcNow;
        var entity = new CoachingLineup
        {
            LineupId = Guid.NewGuid(),
            TeamId = team.TeamId,
            EventId = linkedEvent?.EventId,
            SeasonId = linkedEvent?.SeasonId,
            CreatedBy = callerUserId,
            Title = request.Title.Trim(),
            Formation = request.Formation?.Trim(),
            GameModel = request.GameModel?.Trim(),
            TacticalNotes = request.TacticalNotes?.Trim(),
            Visibility = ParseVisibility(request.Visibility, PlanVisibility.Draft),
            CreatedAt = now,
            UpdatedAt = now
        };

        ApplyLineupPlayers(entity, request.Players);
        _db.CoachingLineups.Add(entity);
        await _db.SaveChangesAsync();
        await NotifyVisiblePlanAsync(team, callerUserId, entity.LineupId, entity.Title, entity.Visibility, "LineupCreated", "New lineup");
        return await BuildLineupDtoAsync(entity.LineupId);
    }

    public async Task<List<LineupDto>> GetTeamLineupsAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var callerRole = await GetCallerTeamRoleAsync(team.TeamId, callerUserId);
        var isAdmin = await IsAdminAsync(callerUserId);

        var query = _db.CoachingLineups
            .Include(l => l.Creator)
            .Include(l => l.Event)
            .Include(l => l.Players)
                .ThenInclude(p => p.Player)
            .Where(l => l.TeamId == teamId);

        if (!isAdmin)
        {
            query = query.Where(l =>
                (callerRole == RoleNameType.Coach && l.CreatedBy == callerUserId) ||
                l.Visibility == PlanVisibility.TeamVisible ||
                l.Visibility == PlanVisibility.PlayerAssigned);
        }

        var lineups = await query.OrderByDescending(l => l.UpdatedAt).ToListAsync();
        return lineups.Select(ToLineupDto).ToList();
    }

    public async Task<LineupDto> GetLineupAsync(Guid clubId, Guid teamId, Guid lineupId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var lineup = await _db.CoachingLineups
            .FirstOrDefaultAsync(l => l.LineupId == lineupId && l.TeamId == teamId)
            ?? throw new InvalidOperationException("Lineup not found.");

        await EnsureCanViewLineupAsync(team, lineup, callerUserId);
        return await BuildLineupDtoAsync(lineupId);
    }

    public async Task<LineupDto> UpdateLineupAsync(Guid clubId, Guid teamId, Guid lineupId, Guid callerUserId, UpdateLineupRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        var lineup = await _db.CoachingLineups
            .Include(l => l.Players)
            .FirstOrDefaultAsync(l => l.LineupId == lineupId && l.TeamId == teamId)
            ?? throw new InvalidOperationException("Lineup not found.");

        await EnsureCanEditLineupAsync(team, lineup, callerUserId);
        await EnsureValidLineupPlayersAsync(teamId, request.Players);

        var linkedEvent = await GetLineupEventAsync(teamId, request.EventId);
        lineup.EventId = linkedEvent?.EventId;
        lineup.SeasonId = linkedEvent?.SeasonId;
        lineup.Title = request.Title.Trim();
        lineup.Formation = request.Formation?.Trim();
        lineup.GameModel = request.GameModel?.Trim();
        lineup.TacticalNotes = request.TacticalNotes?.Trim();
        lineup.Visibility = ParseVisibility(request.Visibility, lineup.Visibility);
        lineup.UpdatedAt = DateTime.UtcNow;

        // Delete ALL existing lineup players directly via SQL to bypass the
        // global query filter (which hides rows for soft-deleted users).
        // Without this, EF only tracks the filtered subset and the
        // RemoveRange + re-insert can hit a unique-index or concurrency error.
        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM coaching_lineup_player WHERE lineup_id = {0}", lineupId);

        // Detach any tracked CoachingLineupPlayer entities so EF doesn't
        // try to DELETE them again on SaveChanges.
        foreach (var entry in _db.ChangeTracker.Entries<CoachingLineupPlayer>()
                     .Where(e => e.Entity.LineupId == lineupId)
                     .ToList())
        {
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
        lineup.Players.Clear();

        ApplyLineupPlayers(lineup, request.Players);

        // Force EF to treat the new lineup players as Added instead of Modified.
        // Because LineupPlayerId is a non-empty Guid, EF guesses they are Modified when added to a tracked entity's navigation.
        foreach (var p in lineup.Players)
        {
            _db.Entry(p).State = Microsoft.EntityFrameworkCore.EntityState.Added;
        }

        await _db.SaveChangesAsync();
        await NotifyVisiblePlanAsync(team, callerUserId, lineup.LineupId, lineup.Title, lineup.Visibility, "LineupUpdated", "Lineup updated");
        return await BuildLineupDtoAsync(lineupId);
    }

    public async Task DeleteLineupAsync(Guid clubId, Guid teamId, Guid lineupId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        var lineup = await _db.CoachingLineups
            .FirstOrDefaultAsync(l => l.LineupId == lineupId && l.TeamId == teamId)
            ?? throw new InvalidOperationException("Lineup not found.");

        await EnsureCanEditLineupAsync(team, lineup, callerUserId);

        lineup.DeletedAt = DateTime.UtcNow;
        lineup.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<PlanDto> BuildDtoAsync(Guid planId)
    {
        var p = await _db.CoachingPlans.Include(x => x.Creator)
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.PlanId == planId)
            ?? throw new InvalidOperationException("Plan not found.");

        return new PlanDto
        {
            PlanId = p.PlanId,
            TeamId = p.TeamId,
            Title = p.Title,
            Description = p.Description,
            Content = p.Content,
            Visibility = p.Visibility.ToString(),
            CreatorName = p.Creator.Name,
            CreatedBy = p.CreatedBy,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Documents = p.Documents
                .Where(d => d.DeletedAt == null)
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new PlanDocumentDto
                {
                    DocumentId = d.DocumentId,
                    FileName = d.OriginalFileName,
                    ContentType = d.ContentType,
                    FileSizeBytes = d.FileSize,
                    UploadedAt = d.CreatedAt
                })
                .ToList()
        };
    }

    private async Task<LineupDto> BuildLineupDtoAsync(Guid lineupId)
    {
        var lineup = await _db.CoachingLineups
            .Include(l => l.Creator)
            .Include(l => l.Event)
            .Include(l => l.Players)
                .ThenInclude(lp => lp.Player)
            .FirstOrDefaultAsync(l => l.LineupId == lineupId)
            ?? throw new InvalidOperationException("Lineup not found.");

        return ToLineupDto(lineup);
    }

    private static LineupDto ToLineupDto(CoachingLineup lineup)
    {
        return new LineupDto
        {
            LineupId = lineup.LineupId,
            TeamId = lineup.TeamId,
            EventId = lineup.EventId,
            SeasonId = lineup.SeasonId,
            EventTitle = lineup.Event?.Title,
            EventStartAt = lineup.Event?.StartAt,
            Title = lineup.Title,
            Formation = lineup.Formation,
            GameModel = lineup.GameModel,
            TacticalNotes = lineup.TacticalNotes,
            Visibility = lineup.Visibility.ToString(),
            CreatorName = lineup.Creator.Name,
            CreatedBy = lineup.CreatedBy,
            CreatedAt = lineup.CreatedAt,
            UpdatedAt = lineup.UpdatedAt,
            Players = lineup.Players
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.Player.Name)
                .Select(p => new LineupPlayerDto
                {
                    LineupPlayerId = p.LineupPlayerId,
                    PlayerUserId = p.PlayerUserId,
                    PlayerName = p.Player.Name,
                    Position = p.Position,
                    Unit = p.Unit,
                    SortOrder = p.SortOrder,
                    Instructions = p.Instructions
                })
                .ToList()
        };
    }

    private async Task<RoleNameType?> GetCallerTeamRoleAsync(Guid teamId, Guid userId)
    {
        var membership = await _db.TeamMemberships
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active);
        return membership?.Role;
    }

    private async Task<Event?> GetLineupEventAsync(Guid teamId, Guid? eventId)
    {
        if (!eventId.HasValue) return null;
        return await _db.Events.FirstOrDefaultAsync(e => e.EventId == eventId.Value && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found for this team.");
    }

    private async Task EnsureValidLineupPlayersAsync(Guid teamId, List<LineupPlayerRequest> players)
    {
        if (players.Count == 0)
            throw new InvalidOperationException("Add at least one player to the lineup.");

        if (players.GroupBy(p => p.PlayerUserId).Any(g => g.Count() > 1))
            throw new InvalidOperationException("A player can only appear once in a lineup.");

        var activePlayerIds = await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.Role == RoleNameType.Player && tm.Status == MembershipStatus.Active)
            .Select(tm => tm.UserId)
            .ToListAsync();

        if (players.Any(p => !activePlayerIds.Contains(p.PlayerUserId)))
            throw new InvalidOperationException("All lineup players must be active players on this team.");
    }

    private static void ApplyLineupPlayers(CoachingLineup lineup, List<LineupPlayerRequest> players)
    {
        foreach (var player in players.OrderBy(p => p.SortOrder))
        {
            lineup.Players.Add(new CoachingLineupPlayer
            {
                LineupPlayerId = Guid.NewGuid(),
                LineupId = lineup.LineupId,
                PlayerUserId = player.PlayerUserId,
                Position = string.IsNullOrWhiteSpace(player.Position) ? "Unassigned" : player.Position.Trim(),
                Unit = string.IsNullOrWhiteSpace(player.Unit) ? "Starting" : player.Unit.Trim(),
                SortOrder = player.SortOrder,
                Instructions = player.Instructions?.Trim()
            });
        }
    }

    private async Task EnsureCanCreatePlanAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;
        var role = await GetCallerTeamRoleAsync(team.TeamId, callerUserId);
        if (role == RoleNameType.Coach) return;
        throw new UnauthorizedAccessException("Only coaches can create plans and lineups.");
    }

    private async Task EnsureCanEditPlanAsync(Team team, CoachingPlan plan, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;
        var role = await GetCallerTeamRoleAsync(team.TeamId, callerUserId);
        if (role == RoleNameType.Coach && plan.CreatedBy == callerUserId) return;
        throw new UnauthorizedAccessException("Only the plan creator can edit this plan.");
    }

    private async Task EnsureCanEditLineupAsync(Team team, CoachingLineup lineup, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;
        var role = await GetCallerTeamRoleAsync(team.TeamId, callerUserId);
        if (role == RoleNameType.Coach && lineup.CreatedBy == callerUserId) return;
        throw new UnauthorizedAccessException("Only the lineup creator can edit this lineup.");
    }

    private async Task EnsureCanViewPlanAsync(Team team, CoachingPlan plan, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;

        var role = await GetCallerTeamRoleAsync(team.TeamId, callerUserId);
        if (role == RoleNameType.Coach && plan.CreatedBy == callerUserId) return;

        var isStaff = role is RoleNameType.TeamManager or RoleNameType.Coach
            or RoleNameType.FitnessCoach or RoleNameType.TeamAnalyst or RoleNameType.TeamDoctor;

        if (isStaff && plan.Visibility is PlanVisibility.TeamVisible or PlanVisibility.PlayerAssigned) return;
        if (plan.Visibility is PlanVisibility.TeamVisible or PlanVisibility.PlayerAssigned) return;
        throw new UnauthorizedAccessException("You do not have access to this plan.");
    }

    private async Task EnsureCanViewLineupAsync(Team team, CoachingLineup lineup, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;

        var role = await GetCallerTeamRoleAsync(team.TeamId, callerUserId);
        if (role == RoleNameType.Coach && lineup.CreatedBy == callerUserId) return;

        var isStaff = role is RoleNameType.TeamManager or RoleNameType.Coach
            or RoleNameType.FitnessCoach or RoleNameType.TeamAnalyst or RoleNameType.TeamDoctor;

        if (isStaff && lineup.Visibility is PlanVisibility.TeamVisible or PlanVisibility.PlayerAssigned) return;
        if (lineup.Visibility is PlanVisibility.TeamVisible or PlanVisibility.PlayerAssigned) return;
        throw new UnauthorizedAccessException("You do not have access to this lineup.");
    }

    private async Task<Team> GetTeamForClubAsync(Guid clubId, Guid teamId) =>
        await _db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
        ?? throw new InvalidOperationException("Team not found.");

    private async Task EnsureCanViewTeamAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;
        if (team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId)) return;
        var hasClubMembership = team.ClubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == team.ClubId && cm.UserId == callerUserId && cm.Status == MembershipStatus.Active);
        var hasTeamMembership = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId && tm.UserId == callerUserId && tm.Status == MembershipStatus.Active);
        if (!hasClubMembership && !hasTeamMembership)
            throw new UnauthorizedAccessException("You do not have access to this team.");
    }

    private static PlanVisibility ParseVisibility(string? value, PlanVisibility fallback)
    {
        return Enum.TryParse<PlanVisibility>(value, true, out var visibility) ? visibility : fallback;
    }

    private Task<bool> IsAdminAsync(Guid userId) =>
        _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);

    private Task NotifyVisiblePlanAsync(Team team, Guid actorUserId, Guid targetId, string targetTitle, PlanVisibility visibility, string type, string title)
    {
        if (visibility == PlanVisibility.Draft)
            return Task.CompletedTask;

        return _notifications.CreateForTeamAsync(team.TeamId, actorUserId, new CreateNotificationRequest
        {
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = type,
            Priority = "Normal",
            DeliveryPolicy = "RealtimeIfConnected",
            Title = title,
            Body = targetTitle,
            TargetType = type.StartsWith("Lineup") ? "Lineup" : "Plan",
            TargetId = targetId,
            TargetRoute = type.StartsWith("Lineup") ? $"/teams/{team.TeamId}/lineups/{targetId}" : $"/teams/{team.TeamId}/plans/{targetId}"
        });
    }
}
