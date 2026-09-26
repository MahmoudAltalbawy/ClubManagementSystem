using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class PlayerService : IPlayerService
{
    private readonly AppDbContext _db;

    public PlayerService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PlayerProfileDto> GetMyProfileAsync(Guid callerUserId)
    {
        var profile = await _db.PlayerProfiles
            .Include(pp => pp.User)
            .FirstOrDefaultAsync(pp => pp.UserId == callerUserId)
            ?? throw new InvalidOperationException("Player profile not found.");

        return await BuildPlayerProfileDtoAsync(profile);
    }

    public async Task<List<PlayerProfileDto>> GetTeamPlayersAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        await EnsureCanViewTeamPlayersAsync(clubId, teamId, callerUserId);

        var playerIds = await _db.PlayerTeams
            .Where(pt => pt.TeamId == teamId && pt.IsCurrent)
            .Select(pt => pt.PlayerId)
            .ToListAsync();

        var profiles = await _db.PlayerProfiles
            .Include(pp => pp.User)
            .Where(pp => playerIds.Contains(pp.PlayerId))
            .OrderBy(pp => pp.User.Name)
            .ToListAsync();

        var result = new List<PlayerProfileDto>(profiles.Count);
        foreach (var profile in profiles)
        {
            result.Add(await BuildPlayerProfileDtoAsync(profile));
        }

        return result;
    }

    public async Task<PlayerProfileDto> GetPlayerProfileAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId)
    {
        await EnsureCanViewTeamPlayersAsync(clubId, teamId, callerUserId);

        var profile = await _db.PlayerProfiles
            .Include(pp => pp.User)
            .FirstOrDefaultAsync(pp => pp.UserId == playerUserId)
            ?? throw new InvalidOperationException("Player profile not found.");

        var belongsToTeam = await _db.PlayerTeams.AnyAsync(pt =>
            pt.PlayerId == profile.PlayerId &&
            pt.TeamId == teamId &&
            pt.IsCurrent);

        if (!belongsToTeam)
            throw new InvalidOperationException("Player is not on this team.");

        return await BuildPlayerProfileDtoAsync(profile);
    }

    public async Task<PlayerProfileDto> UpsertPlayerProfileAsync(
        Guid clubId,
        Guid teamId,
        Guid playerUserId,
        Guid callerUserId,
        UpsertPlayerProfileRequest request)
    {
        await EnsureCanManagePlayerProfileAsync(clubId, teamId, callerUserId);

        var playerMembership = await _db.TeamMemberships
            .FirstOrDefaultAsync(tm =>
                tm.TeamId == teamId &&
                tm.UserId == playerUserId &&
                tm.Role == RoleNameType.Player &&
                tm.Status == MembershipStatus.Active)
            ?? throw new InvalidOperationException("Player is not an active member of this team.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == playerUserId)
            ?? throw new InvalidOperationException("User not found.");

        var profile = await _db.PlayerProfiles
            .IgnoreQueryFilters()
            .Include(pp => pp.User)
            .FirstOrDefaultAsync(pp => pp.UserId == playerUserId);

        var now = DateTime.UtcNow;
        var previousHeight = profile?.Height;
        var previousWeight = profile?.Weight;

        if (profile == null)
        {
            profile = new PlayerProfile
            {
                PlayerId = Guid.NewGuid(),
                UserId = playerUserId,
                Position = request.Position.Trim(),
                JerseyNumber = request.JerseyNumber,
                Height = request.Height,
                Weight = request.Weight,
                CreatedAt = now,
                UpdatedAt = now,
                User = user
            };
            _db.PlayerProfiles.Add(profile);
        }
        else
        {
            profile.Position = request.Position.Trim();
            profile.JerseyNumber = request.JerseyNumber;
            profile.Height = request.Height;
            profile.Weight = request.Weight;
            profile.DeletedAt = null;
            profile.UpdatedAt = now;
        }

        var heightChanged = previousHeight.HasValue && previousHeight != request.Height;
        var weightChanged = previousWeight.HasValue && previousWeight != request.Weight;
        if (heightChanged || weightChanged)
        {
            _db.FitnessRecords.Add(new FitnessRecord
            {
                FitnessId = Guid.NewGuid(),
                TeamId = teamId,
                PlayerId = profile.PlayerId,
                FitnessUserId = callerUserId,
                TestDate = now,
                Height = heightChanged ? previousHeight : null,
                Weight = weightChanged ? previousWeight : null,
                CreatedBy = callerUserId,
                UpdatedBy = callerUserId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var hasCurrentPlayerTeam = await _db.PlayerTeams.AnyAsync(pt =>
            pt.PlayerId == profile.PlayerId &&
            pt.TeamId == teamId &&
            pt.IsCurrent);

        if (!hasCurrentPlayerTeam)
        {
            _db.PlayerTeams.Add(new PlayerTeam
            {
                Id = Guid.NewGuid(),
                PlayerId = profile.PlayerId,
                TeamId = teamId,
                JoinedDate = DateOnly.FromDateTime(DateTime.UtcNow),
                IsCurrent = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        return await BuildPlayerProfileDtoAsync(profile);
    }

    private async Task<PlayerProfileDto> BuildPlayerProfileDtoAsync(PlayerProfile profile)
    {
        var currentTeam = await _db.PlayerTeams
            .Where(pt => pt.PlayerId == profile.PlayerId && pt.IsCurrent)
            .Join(_db.Teams,
                pt => pt.TeamId,
                t => t.TeamId,
                (pt, t) => new { t.TeamId, t.TeamName })
            .FirstOrDefaultAsync();

        return new PlayerProfileDto
        {
            PlayerId = profile.PlayerId,
            UserId = profile.UserId,
            Name = profile.User.Name,
            Email = profile.User.Email,
            Username = profile.User.Username,
            Bio = profile.User.Bio,
            ProfileImageUrl = profile.User.ProfileImageUrl,
            Dob = profile.User.Dob,
            Position = profile.Position,
            JerseyNumber = profile.JerseyNumber,
            Height = profile.Height,
            Weight = profile.Weight,
            CurrentTeamId = currentTeam?.TeamId,
            CurrentTeamName = currentTeam?.TeamName
        };
    }

    private async Task EnsureCanViewTeamPlayersAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var isAdmin = await _db.Users.AnyAsync(u => u.UserId == callerUserId && u.IsAdmin);
        if (isAdmin)
            return;

        var isClubManager = await _db.Clubs.AnyAsync(c => c.ClubId == clubId && c.CreatedBy == callerUserId);
        var isTeamMember = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == teamId &&
            tm.UserId == callerUserId &&
            tm.Status == MembershipStatus.Active);

        if (!isClubManager && !isTeamMember)
            throw new UnauthorizedAccessException("You do not have access to this team's players.");
    }

    private async Task EnsureCanManagePlayerProfileAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var isAdmin = await _db.Users.AnyAsync(u => u.UserId == callerUserId && u.IsAdmin);
        if (isAdmin)
            return;

        var isClubManager = await _db.Clubs.AnyAsync(c => c.ClubId == clubId && c.CreatedBy == callerUserId);
        var isTeamManagerOrFitnessCoach = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == teamId &&
            tm.UserId == callerUserId &&
            tm.Status == MembershipStatus.Active &&
            (tm.Role == RoleNameType.TeamManager || tm.Role == RoleNameType.FitnessCoach));

        if (!isClubManager && !isTeamManagerOrFitnessCoach)
            throw new UnauthorizedAccessException("Only a club manager, team manager, or fitness coach can manage player profiles.");
    }
}
