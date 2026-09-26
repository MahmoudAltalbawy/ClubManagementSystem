using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class TeamService : ITeamService
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IFileStorageService _fileStorage;

    public TeamService(AppDbContext db, ITokenService tokenService, IFileStorageService fileStorage)
    {
        _db = db;
        _tokenService = tokenService;
        _fileStorage = fileStorage;
    }

    public async Task<List<TeamCategoryDto>> GetTeamCategoriesAsync()
    {
        var categories = new List<TeamCategoryDto>();

        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT category_id, name, min_age, max_age
                FROM public.team_category
                ORDER BY min_age NULLS FIRST, max_age NULLS FIRST, name
                """;

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                categories.Add(new TeamCategoryDto
                {
                    CategoryId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    MinAge = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    MaxAge = reader.IsDBNull(3) ? null : reader.GetInt32(3)
                });
            }

            return categories;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    public async Task<TeamDto> CreateTeamAsync(Guid clubId, Guid callerUserId, CreateTeamRequest request, Stream? imageStream = null, string? imageFileName = null)
    {
        var club = await _db.Clubs
            .FirstOrDefaultAsync(c => c.ClubId == clubId && c.DeletedAt == null)
            ?? throw new InvalidOperationException("Club not found.");

        await EnsureCanManageClubAsync(clubId, callerUserId);

        var categoryExists = await TeamCategoryExistsAsync(request.CategoryId);
        if (!categoryExists)
            throw new InvalidOperationException("Team category not found.");

        ValidateSeasonDetails(request);

        var duplicate = await _db.Teams
            .IgnoreQueryFilters()
            .AnyAsync(t =>
                t.ClubId == clubId &&
                t.TeamName == request.TeamName &&
                t.CategoryId == request.CategoryId &&
                t.DeletedAt == null);

        if (duplicate)
            throw new InvalidOperationException(
                $"A team named '{request.TeamName}' already exists in this category for this club.");

        var now = DateTime.UtcNow;
        var team = new Team
        {
            TeamId = Guid.NewGuid(),
            ClubId = club.ClubId,
            TeamName = request.TeamName.Trim(),
            ImageUrl = imageStream != null && !string.IsNullOrWhiteSpace(imageFileName)
                ? await _fileStorage.SaveFileAsync(imageStream, imageFileName, "teams")
                : request.ImageUrl,
            CategoryId = request.CategoryId,
            CreatedBy = callerUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Teams.Add(team);

        var callerIsClubOwner = club.CreatedBy == callerUserId;
        var callerIsAdmin = await IsAdminAsync(callerUserId);
        if (!callerIsClubOwner && !callerIsAdmin)
        {
            _db.TeamMemberships.Add(new TeamMembership
            {
                TeamMembershipId = Guid.NewGuid(),
                TeamId = team.TeamId,
                UserId = callerUserId,
                Role = RoleNameType.TeamManager,
                Status = MembershipStatus.Active,
                InvitedBy = callerUserId,
                JoinedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        _db.Seasons.Add(CreateSeason(team.TeamId, callerUserId, request, now));

        await _db.SaveChangesAsync();

        return await BuildTeamDtoAsync(team.TeamId);
    }

    public async Task<List<TeamDto>> GetClubTeamsAsync(Guid clubId, Guid callerUserId)
    {
        await EnsureCanViewClubAsync(clubId, callerUserId);

        var teamIds = await _db.Teams
            .Where(t => t.ClubId == clubId && t.DeletedAt == null)
            .OrderBy(t => t.TeamName)
            .Select(t => t.TeamId)
            .ToListAsync();

        var teams = new List<TeamDto>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            teams.Add(await BuildTeamDtoAsync(teamId));
        }

        return teams;
    }

    public async Task<List<TeamDto>> GetMyTeamsAsync(Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
        {
            var adminTeamIds = await _db.Teams
                .Where(t => t.DeletedAt == null)
                .OrderBy(t => t.Club == null ? "" : t.Club.Name)
                .ThenBy(t => t.TeamName)
                .Select(t => t.TeamId)
                .ToListAsync();

            var adminTeams = new List<TeamDto>(adminTeamIds.Count);
            foreach (var teamId in adminTeamIds)
            {
                var team = await BuildTeamDtoAsync(teamId);
                team.MyRole = RoleNameType.Admin.ToString();
                adminTeams.Add(team);
            }

            return adminTeams;
        }

        var memberships = await _db.TeamMemberships
            .Include(tm => tm.Team)
                .ThenInclude(t => t.Club)
            .Where(tm =>
                tm.UserId == callerUserId &&
                tm.Status == MembershipStatus.Active &&
                tm.Team.DeletedAt == null &&
                (!tm.Team.ClubId.HasValue || tm.Team.Club!.CreatedBy != callerUserId))
            .OrderBy(tm => tm.Team.Club == null ? "" : tm.Team.Club.Name)
            .ThenBy(tm => tm.Team.TeamName)
            .Select(tm => new { tm.TeamId, tm.Role })
            .ToListAsync();

        var ownedClubTeamIds = await _db.Teams
            .Where(t =>
                t.DeletedAt == null &&
                t.ClubId.HasValue &&
                t.Club!.CreatedBy == callerUserId)
            .OrderBy(t => t.Club == null ? "" : t.Club.Name)
            .ThenBy(t => t.TeamName)
            .Select(t => t.TeamId)
            .ToListAsync();

        var teams = new List<TeamDto>(memberships.Count + ownedClubTeamIds.Count);
        var includedTeamIds = new HashSet<Guid>();
        foreach (var membership in memberships)
        {
            if (!includedTeamIds.Add(membership.TeamId))
                continue;

            var team = await BuildTeamDtoAsync(membership.TeamId);
            team.MyRole = membership.Role.ToString();
            teams.Add(team);
        }

        foreach (var teamId in ownedClubTeamIds)
        {
            if (!includedTeamIds.Add(teamId))
                continue;

            var team = await BuildTeamDtoAsync(teamId);
            team.MyRole = RoleNameType.ClubManager.ToString();
            teams.Add(team);
        }

        return teams
            .OrderBy(t => t.ClubName ?? "")
            .ThenBy(t => t.TeamName)
            .ToList();
    }

    public async Task<TeamDto> GetTeamAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        await EnsureCanViewClubAsync(clubId, callerUserId);

        var teamExists = await _db.Teams
            .AnyAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null);

        if (!teamExists)
            throw new InvalidOperationException("Team not found.");

        return await BuildTeamDtoAsync(teamId);
    }

    public async Task DeleteTeamAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await _db.Teams
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");

        await EnsureCanManageTeamAsync(team, callerUserId);

        var now = DateTime.UtcNow;
        team.DeletedAt = now;
        team.UpdatedAt = now;

        var activeMemberships = await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.Status == MembershipStatus.Active)
            .ToListAsync();

        foreach (var membership in activeMemberships)
        {
            if (membership.Role == RoleNameType.Player)
            {
                var playerProfile = await _db.PlayerProfiles
                    .FirstOrDefaultAsync(pp => pp.UserId == membership.UserId);

                if (playerProfile != null)
                {
                    var playerTeams = await _db.PlayerTeams
                        .Where(pt => pt.PlayerId == playerProfile.PlayerId && pt.TeamId == teamId && pt.IsCurrent)
                        .ToListAsync();

                    foreach (var playerTeam in playerTeams)
                    {
                        playerTeam.IsCurrent = false;
                        playerTeam.LeftDate = DateOnly.FromDateTime(now);
                        playerTeam.UpdatedAt = now;
                    }
                }
            }

            membership.Status = MembershipStatus.Revoked;
            membership.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<TeamMemberDto>> GetTeamMembersAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await _db.Teams
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");

        await EnsureCanViewTeamAsync(team, callerUserId);

        var members = await _db.TeamMemberships
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == teamId && tm.Status == MembershipStatus.Active)
            .OrderBy(tm => tm.JoinedAt)
            .Select(tm => new TeamMemberDto
            {
                UserId = tm.UserId,
                Name = tm.User.Name,
                Email = tm.User.Email,
                PhoneNumber = tm.User.PhoneNumber,
                ProfileImageUrl = tm.User.ProfileImageUrl,
                Role = tm.Role.ToString(),
                JoinedAt = tm.JoinedAt
            })
            .ToListAsync();

        var playerUserIds = members
            .Where(m => m.Role == RoleNameType.Player.ToString())
            .Select(m => m.UserId)
            .ToList();

        if (playerUserIds.Count == 0)
            return members;

        var playerProfiles = await _db.PlayerProfiles
            .Where(pp => playerUserIds.Contains(pp.UserId) && pp.DeletedAt == null)
            .Select(pp => new { pp.UserId, pp.PlayerId, pp.Position, pp.JerseyNumber })
            .ToListAsync();

        var playerIdByUserId = playerProfiles.ToDictionary(pp => pp.UserId, pp => pp.PlayerId);
        var profileByUserId = playerProfiles.ToDictionary(pp => pp.UserId);
        var playerIds = playerProfiles.Select(pp => pp.PlayerId).ToList();
        if (playerIds.Count == 0)
            return members;

        var latestUnclearedInjuries = await _db.MedicalRecords
            .Where(mr => mr.TeamId == teamId && playerIds.Contains(mr.PlayerId) && !mr.IsCleared)
            .OrderByDescending(mr => mr.RecordDate)
            .Select(mr => new { mr.PlayerId, mr.InjuryType })
            .ToListAsync();

        var injuryByPlayerId = latestUnclearedInjuries
            .GroupBy(mr => mr.PlayerId)
            .ToDictionary(g => g.Key, g => g.First().InjuryType);

        foreach (var member in members)
        {
            if (profileByUserId.TryGetValue(member.UserId, out var profile))
            {
                member.Position = profile.Position;
                member.JerseyNumber = profile.JerseyNumber;
            }

            if (!playerIdByUserId.TryGetValue(member.UserId, out var playerId))
                continue;
            if (!injuryByPlayerId.TryGetValue(playerId, out var injuryType))
                continue;

            member.IsInjured = true;
            member.InjuryType = injuryType;
        }

        return members;
    }

    public async Task RemoveTeamMemberAsync(Guid clubId, Guid teamId, Guid targetUserId, Guid callerUserId)
    {
        var team = await _db.Teams
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");

        if (callerUserId != targetUserId)
            await EnsureCanManageTeamAsync(team, callerUserId);

        var membership = await _db.TeamMemberships
            .FirstOrDefaultAsync(tm =>
                tm.TeamId == teamId &&
                tm.UserId == targetUserId &&
                tm.Status == MembershipStatus.Active)
            ?? throw new InvalidOperationException("Team membership not found.");

        if (membership.Role == RoleNameType.TeamManager)
        {
            var activeManagerCount = await _db.TeamMemberships.CountAsync(tm =>
                tm.TeamId == teamId &&
                tm.Role == RoleNameType.TeamManager &&
                tm.Status == MembershipStatus.Active);

            if (activeManagerCount <= 1)
                throw new InvalidOperationException("A team must keep at least one active team manager.");
        }

        membership.Status = MembershipStatus.Revoked;
        membership.UpdatedAt = DateTime.UtcNow;

        if (membership.Role == RoleNameType.Player)
        {
            var playerProfile = await _db.PlayerProfiles
                .FirstOrDefaultAsync(pp => pp.UserId == targetUserId);

            if (playerProfile != null)
            {
                var playerTeams = await _db.PlayerTeams
                    .Where(pt => pt.PlayerId == playerProfile.PlayerId && pt.TeamId == teamId && pt.IsCurrent)
                    .ToListAsync();

                foreach (var playerTeam in playerTeams)
                {
                    playerTeam.IsCurrent = false;
                    playerTeam.LeftDate = DateOnly.FromDateTime(DateTime.UtcNow);
                    playerTeam.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        await _db.SaveChangesAsync();
        await _tokenService.RevokeAllUserTokensAsync(targetUserId);
    }

    private async Task<TeamDto> BuildTeamDtoAsync(Guid teamId)
    {
        var team = await _db.Teams
            .Include(t => t.Club)
            .Include(t => t.Creator)
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");

        var managers = await _db.TeamMemberships
            .Include(tm => tm.User)
            .Where(tm =>
                tm.TeamId == teamId &&
                tm.Role == RoleNameType.TeamManager &&
                tm.Status == MembershipStatus.Active)
            .OrderBy(tm => tm.JoinedAt)
            .Select(tm => new ManagerSummaryDto
            {
                UserId = tm.UserId,
                Name = tm.User.Name,
                Email = tm.User.Email
            })
            .ToListAsync();

        var memberCount = await _db.TeamMemberships
            .CountAsync(tm => tm.TeamId == teamId && tm.Status == MembershipStatus.Active);

        return new TeamDto
        {
            TeamId = team.TeamId,
            ClubId = team.ClubId,
            ClubName = team.Club?.Name,
            ClubLogoUrl = team.Club?.LogoUrl,
            TeamName = team.TeamName,
            ImageUrl = team.ImageUrl,
            CategoryId = team.CategoryId,
            CreatedBy = team.CreatedBy,
            CreatorName = team.Creator?.Name,
            MemberCount = memberCount,
            CreatedAt = team.CreatedAt,
            Managers = managers
        };
    }

    private async Task EnsureCanManageClubAsync(Guid clubId, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        var isClubManager = await _db.Clubs.AnyAsync(c => c.ClubId == clubId && c.CreatedBy == callerUserId);
        var isClubTeamManager = await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId &&
            cm.UserId == callerUserId &&
            cm.Role == RoleNameType.TeamManager &&
            cm.Status == MembershipStatus.Active);

        if (!isClubManager && !isClubTeamManager)
            throw new UnauthorizedAccessException("You do not have permission to manage this club.");
    }

    private async Task EnsureCanViewClubAsync(Guid clubId, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        var isClubManager = await _db.Clubs.AnyAsync(c => c.ClubId == clubId && c.CreatedBy == callerUserId);
        var isClubMember = await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId &&
            cm.UserId == callerUserId &&
            cm.Status == MembershipStatus.Active);
        var isTeamMember = await _db.TeamMemberships.AnyAsync(tm =>
            tm.UserId == callerUserId &&
            tm.Status == MembershipStatus.Active &&
            tm.Team.ClubId == clubId);

        if (!isClubManager && !isClubMember && !isTeamMember)
            throw new UnauthorizedAccessException("You do not have access to this club.");
    }

    private async Task EnsureCanManageTeamAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        if (team.ClubId.HasValue &&
            await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId))
            return;

        var isTeamManager = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Role == RoleNameType.TeamManager &&
            tm.Status == MembershipStatus.Active);

        if (!isTeamManager)
            throw new UnauthorizedAccessException("You do not have permission to manage this team.");
    }

    private async Task EnsureCanViewTeamAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        if (team.ClubId.HasValue &&
            await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId))
            return;

        var hasClubMembership = team.ClubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == team.ClubId &&
            cm.UserId == callerUserId &&
            cm.Status == MembershipStatus.Active);

        var hasTeamMembership = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Status == MembershipStatus.Active);

        if (!hasClubMembership && !hasTeamMembership)
            throw new UnauthorizedAccessException("You do not have access to this team.");
    }

    private Task<bool> IsAdminAsync(Guid userId)
    {
        return _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);
    }

    private async Task<bool> TeamCategoryExistsAsync(Guid categoryId)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM public.team_category WHERE category_id = @categoryId LIMIT 1";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@categoryId";
            parameter.Value = categoryId;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync();
            return result != null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static void ValidateSeasonDetails(CreateTeamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SeasonLabel))
            throw new InvalidOperationException("Season label is required.");

        if (!request.SeasonStartDate.HasValue || !request.SeasonEndDate.HasValue)
            throw new InvalidOperationException("Season start and end dates are required.");

        if (request.SeasonEndDate.Value <= request.SeasonStartDate.Value)
            throw new InvalidOperationException("Season end date must be after the start date.");
    }

    private static Season CreateSeason(Guid teamId, Guid createdBy, CreateTeamRequest request, DateTime nowUtc)
    {
        return new Season
        {
            SeasonId = Guid.NewGuid(),
            TeamId = teamId,
            CreatedBy = createdBy,
            Label = request.SeasonLabel.Trim(),
            StartDate = request.SeasonStartDate!.Value,
            EndDate = request.SeasonEndDate!.Value,
            IsCurrent = true,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };
    }
}
