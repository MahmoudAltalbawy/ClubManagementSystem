using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class InvitationService : IInvitationService
{
    private static readonly RoleNameType[] AllowedTeamInvitationRoles =
    [
        RoleNameType.TeamManager,
        RoleNameType.Coach,
        RoleNameType.FitnessCoach,
        RoleNameType.TeamAnalyst,
        RoleNameType.TeamDoctor,
        RoleNameType.Player
    ];

    private readonly AppDbContext _db;
    private readonly IEmailService _emailService;
    private readonly ITokenService _tokenService;

    public InvitationService(AppDbContext db, IEmailService emailService, ITokenService tokenService)
    {
        _db = db;
        _emailService = emailService;
        _tokenService = tokenService;
    }

    public async Task<InvitationDto> CreateClubInvitationAsync(Guid clubId, CreateInvitationRequest request, Guid invitedBy)
    {
        var club = await _db.Clubs
            .Include(c => c.Creator)
            .FirstOrDefaultAsync(c => c.ClubId == clubId)
            ?? throw new InvalidOperationException("Club not found.");

        var isAdmin = await IsAdminAsync(invitedBy);
        if (!isAdmin && club.CreatedBy != invitedBy)
            throw new UnauthorizedAccessException("Only the club manager or an admin can create club invitations.");

        if (request.RoleName != RoleNameType.TeamManager && request.RoleName != RoleNameType.ClubManager)
            throw new InvalidOperationException("Club invitations can only assign the TeamManager or ClubManager role.");

        var invitation = new Invitation
        {
            InvitationId = Guid.NewGuid(),
            Token = GenerateInvitationToken(),
            Email = request.Email.Trim(),
            Role = request.RoleName,
            ClubId = clubId,
            TeamId = null,
            InvitedBy = invitedBy,
            Status = InvitationStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };

        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync();

        await _emailService.SendInvitationEmailAsync(
            invitation.Email,
            invitation.Token,
            club.Name,
            invitation.Role.ToString(),
            club.Creator.Name);

        return await GetInvitationAsync(invitation.Token);
    }

    public async Task<InvitationDto> CreateTeamInvitationAsync(Guid clubId, Guid teamId, CreateInvitationRequest request, Guid invitedBy)
    {
        var team = await _db.Teams
            .Include(t => t.Club)
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId)
            ?? throw new InvalidOperationException("Team not found.");

        await EnsureTeamInvitationAuthorityAsync(team, invitedBy);

        if (!AllowedTeamInvitationRoles.Contains(request.RoleName))
            throw new InvalidOperationException("The selected role is not allowed for team invitations.");

        if (request.RoleName == RoleNameType.Player)
        {
            if (string.IsNullOrWhiteSpace(request.PlayerPosition))
                throw new InvalidOperationException("Player position is required when inviting a player.");

            if (!request.JerseyNumber.HasValue)
                throw new InvalidOperationException("Jersey number is required when inviting a player.");
        }

        var invitation = new Invitation
        {
            InvitationId = Guid.NewGuid(),
            Token = GenerateInvitationToken(),
            Email = request.Email.Trim(),
            Role = request.RoleName,
            ClubId = clubId,
            TeamId = teamId,
            InvitedBy = invitedBy,
            Status = InvitationStatus.Pending,
            PlayerPosition = request.PlayerPosition?.Trim(),
            JerseyNumber = request.JerseyNumber,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };

        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync();

        var inviterName = await _db.Users
            .Where(u => u.UserId == invitedBy)
            .Select(u => u.Name)
            .FirstAsync();

        await _emailService.SendInvitationEmailAsync(
            invitation.Email,
            invitation.Token,
            team.TeamName,
            invitation.Role.ToString(),
            inviterName);

        return await GetInvitationAsync(invitation.Token);
    }

    public async Task<InvitationDto> GetInvitationAsync(string token)
    {
        var invitation = await _db.Invitations
            .Include(i => i.Club)
            .Include(i => i.Team)
            .Include(i => i.InvitedByUser)
            .FirstOrDefaultAsync(i => i.Token == token)
            ?? throw new InvalidOperationException("Invitation not found.");

        if (invitation.Status == InvitationStatus.Pending && invitation.ExpiresAt <= DateTime.UtcNow)
        {
            invitation.Status = InvitationStatus.Expired;
            invitation.ResolvedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return MapInvitation(invitation);
    }

    public async Task<List<InvitationDto>> GetMyPendingInvitationsAsync(Guid userId)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.UserId == userId)
            ?? throw new InvalidOperationException("User not found.");

        var now = DateTime.UtcNow;
        var expiredInvitations = await _db.Invitations
            .Where(i =>
                i.Status == InvitationStatus.Pending &&
                i.ExpiresAt <= now &&
                i.Email.ToLower() == user.Email.ToLower())
            .ToListAsync();

        if (expiredInvitations.Count > 0)
        {
            var resolvedAt = DateTime.UtcNow;
            foreach (var invitation in expiredInvitations)
            {
                invitation.Status = InvitationStatus.Expired;
                invitation.ResolvedAt = resolvedAt;
            }

            await _db.SaveChangesAsync();
        }

        return await _db.Invitations
            .Include(i => i.Club)
            .Include(i => i.Team)
            .Include(i => i.InvitedByUser)
            .Where(i =>
                i.Status == InvitationStatus.Pending &&
                i.ExpiresAt > now &&
                i.Email.ToLower() == user.Email.ToLower())
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationDto
            {
                InvitationId = i.InvitationId,
                Token = i.Token,
                Email = i.Email,
                Role = i.Role.ToString(),
                ClubName = i.Club.Name,
                TeamName = i.Team != null ? i.Team.TeamName : null,
                PlayerPosition = i.PlayerPosition,
                JerseyNumber = i.JerseyNumber,
                InviterName = i.InvitedByUser.Name,
                Status = i.Status.ToString(),
                ExpiresAt = i.ExpiresAt,
                CreatedAt = i.CreatedAt
            })
            .ToListAsync();
    }

    public async Task<InvitationAcceptResultDto> AcceptInvitationAsync(string token, Guid acceptingUserId)
    {
        var invitation = await _db.Invitations
            .Include(i => i.Club)
            .Include(i => i.Team)
            .FirstOrDefaultAsync(i => i.Token == token)
            ?? throw new InvalidOperationException("Invitation not found.");

        if (invitation.Status != InvitationStatus.Pending)
            throw new InvalidOperationException($"Invitation is already {invitation.Status}.");

        if (invitation.ExpiresAt <= DateTime.UtcNow)
        {
            invitation.Status = InvitationStatus.Expired;
            invitation.ResolvedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            throw new InvalidOperationException("Invitation has expired.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == acceptingUserId)
            ?? throw new InvalidOperationException("User not found.");

        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invitation email does not match the signed-in user.");

        await EnsurePlayerConstraintsAsync(user.UserId, invitation.Role);
        if (invitation.Role == RoleNameType.Player && invitation.TeamId.HasValue)
        {
            if (!user.Dob.HasValue)
                throw new InvalidOperationException("Add your date of birth before accepting a player invitation.");

            await EnsurePlayerFitsTeamCategoryAsync(invitation.TeamId.Value, user.Dob.Value);
        }

        if (invitation.TeamId.HasValue)
        {
            await AcceptTeamInvitationAsync(invitation, user);
        }
        else
        {
            await AcceptClubInvitationAsync(invitation, user);
        }

        invitation.Status = InvitationStatus.Accepted;
        invitation.AcceptedAt = DateTime.UtcNow;
        invitation.ResolvedAt = invitation.AcceptedAt;
        invitation.AcceptedByUserId = user.UserId;

        await _db.SaveChangesAsync();
        await _tokenService.RevokeAllUserTokensAsync(user.UserId);

        return new InvitationAcceptResultDto
        {
            Message = "Invitation accepted successfully.",
            ClubId = invitation.ClubId,
            TeamId = invitation.TeamId,
            Role = invitation.Role.ToString(),
            ClubName = invitation.Club.Name,
            TeamName = invitation.Team?.TeamName
        };
    }

    public async Task DenyInvitationAsync(string token, Guid denyingUserId)
    {
        var invitation = await _db.Invitations
            .FirstOrDefaultAsync(i => i.Token == token)
            ?? throw new InvalidOperationException("Invitation not found.");

        if (invitation.Status != InvitationStatus.Pending)
            throw new InvalidOperationException($"Invitation is already {invitation.Status}.");

        if (invitation.ExpiresAt <= DateTime.UtcNow)
        {
            invitation.Status = InvitationStatus.Expired;
            invitation.ResolvedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            throw new InvalidOperationException("Invitation has expired.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == denyingUserId)
            ?? throw new InvalidOperationException("User not found.");

        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invitation email does not match the signed-in user.");

        var denied = await DenyInvitationForEmailAsync(token, user.Email);
        if (!denied)
            throw new InvalidOperationException("Invitation cannot be declined.");
    }

    public async Task<List<InvitationDto>> GetClubInvitationsAsync(Guid clubId, Guid callerUserId)
    {
        var isAdmin = await IsAdminAsync(callerUserId);
        var isClubManager = await _db.Clubs.AnyAsync(c => c.ClubId == clubId && c.CreatedBy == callerUserId);

        if (!isAdmin && !isClubManager)
            throw new UnauthorizedAccessException("Only the club manager or an admin can view club invitations.");

        return await _db.Invitations
            .Include(i => i.Club)
            .Include(i => i.Team)
            .Include(i => i.InvitedByUser)
            .Where(i => i.ClubId == clubId && i.TeamId == null)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationDto
            {
                InvitationId = i.InvitationId,
                Token = i.Token,
                Email = i.Email,
                Role = i.Role.ToString(),
                ClubName = i.Club.Name,
                TeamName = null,
                PlayerPosition = i.PlayerPosition,
                JerseyNumber = i.JerseyNumber,
                InviterName = i.InvitedByUser.Name,
                Status = i.Status.ToString(),
                ExpiresAt = i.ExpiresAt,
                CreatedAt = i.CreatedAt
            })
            .ToListAsync();
    }

    public async Task<List<InvitationDto>> GetTeamInvitationsAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await _db.Teams
            .Include(t => t.Club)
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId)
            ?? throw new InvalidOperationException("Team not found.");

        await EnsureTeamInvitationAuthorityAsync(team, callerUserId);

        return await _db.Invitations
            .Include(i => i.Club)
            .Include(i => i.Team)
            .Include(i => i.InvitedByUser)
            .Where(i => i.ClubId == clubId && i.TeamId == teamId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationDto
            {
                InvitationId = i.InvitationId,
                Token = i.Token,
                Email = i.Email,
                Role = i.Role.ToString(),
                ClubName = i.Club.Name,
                TeamName = i.Team != null ? i.Team.TeamName : null,
                PlayerPosition = i.PlayerPosition,
                JerseyNumber = i.JerseyNumber,
                InviterName = i.InvitedByUser.Name,
                Status = i.Status.ToString(),
                ExpiresAt = i.ExpiresAt,
                CreatedAt = i.CreatedAt
            })
            .ToListAsync();
    }

    public async Task CancelInvitationAsync(Guid invitationId, Guid callerUserId)
    {
        var invitation = await _db.Invitations
            .Include(i => i.Team)
            .FirstOrDefaultAsync(i => i.InvitationId == invitationId)
            ?? throw new InvalidOperationException("Invitation not found.");

        if (invitation.Status != InvitationStatus.Pending)
            throw new InvalidOperationException("Only pending invitations can be cancelled.");

        var isAdmin = await IsAdminAsync(callerUserId);
        var isClubManager = await _db.Clubs.AnyAsync(c => c.ClubId == invitation.ClubId && c.CreatedBy == callerUserId);
        var isTeamManager = invitation.TeamId.HasValue && await IsTeamManagerAsync(invitation.TeamId.Value, callerUserId);

        if (!isAdmin && !isClubManager && invitation.InvitedBy != callerUserId && !isTeamManager)
            throw new UnauthorizedAccessException("You are not allowed to cancel this invitation.");

        invitation.Status = InvitationStatus.Cancelled;
        invitation.ResolvedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task CleanupFinalizedInvitationsAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT public.cleanup_finalized_invitations(@cutoff)";

            var cutoffParameter = command.CreateParameter();
            cutoffParameter.ParameterName = "@cutoff";
            cutoffParameter.Value = cutoffUtc;
            command.Parameters.Add(cutoffParameter);

            await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private async Task AcceptClubInvitationAsync(Invitation invitation, User user)
    {
        // Check for any existing membership (Active, Revoked, or Left) to avoid unique constraint violation
        var existingMembership = await _db.ClubMemberships.FirstOrDefaultAsync(cm =>
            cm.ClubId == invitation.ClubId &&
            cm.UserId == user.UserId);

        if (existingMembership != null && existingMembership.Status == MembershipStatus.Active)
            throw new InvalidOperationException("User is already an active member of this club.");

        if (existingMembership != null)
        {
            // Reactivate the existing membership
            existingMembership.Role = invitation.Role;
            existingMembership.Status = MembershipStatus.Active;
            existingMembership.InvitedBy = invitation.InvitedBy;
            existingMembership.JoinedAt = DateTime.UtcNow;
            existingMembership.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.ClubMemberships.Add(new ClubMembership
            {
                ClubMembershipId = Guid.NewGuid(),
                ClubId = invitation.ClubId,
                UserId = user.UserId,
                Role = invitation.Role,
                Status = MembershipStatus.Active,
                InvitedBy = invitation.InvitedBy,
                JoinedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task AcceptTeamInvitationAsync(Invitation invitation, User user)
    {
        var team = invitation.Team ?? await _db.Teams
            .Include(t => t.Club)
            .FirstAsync(t => t.TeamId == invitation.TeamId);

        if (team.ClubId != invitation.ClubId)
            throw new InvalidOperationException("Invitation team does not belong to the expected club.");

        if (team.ClubId.HasValue)
        {
            var isClubOwner = await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == user.UserId);
            if (isClubOwner)
                throw new InvalidOperationException("The club manager cannot join a team inside their own club.");
        }

        // Check for any existing membership (Active, Revoked, or Left) to avoid unique constraint violation
        var existingMembership = await _db.TeamMemberships.FirstOrDefaultAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == user.UserId);

        if (existingMembership != null && existingMembership.Status == MembershipStatus.Active)
            throw new InvalidOperationException("User is already an active member of this team.");

        if (existingMembership != null)
        {
            // Reactivate the existing membership
            existingMembership.Role = invitation.Role;
            existingMembership.Status = MembershipStatus.Active;
            existingMembership.InvitedBy = invitation.InvitedBy;
            existingMembership.JoinedAt = DateTime.UtcNow;
            existingMembership.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.TeamMemberships.Add(new TeamMembership
            {
                TeamMembershipId = Guid.NewGuid(),
                TeamId = team.TeamId,
                UserId = user.UserId,
                Role = invitation.Role,
                Status = MembershipStatus.Active,
                InvitedBy = invitation.InvitedBy,
                JoinedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        if (invitation.Role == RoleNameType.Player)
        {
            var playerProfile = await _db.PlayerProfiles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(pp => pp.UserId == user.UserId);

            if (playerProfile == null)
            {
                _db.PlayerProfiles.Add(new PlayerProfile
                {
                    PlayerId = Guid.NewGuid(),
                    UserId = user.UserId,
                    Position = invitation.PlayerPosition?.Trim(),
                    JerseyNumber = invitation.JerseyNumber,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                playerProfile.Position = invitation.PlayerPosition?.Trim();
                playerProfile.JerseyNumber = invitation.JerseyNumber;
                playerProfile.DeletedAt = null;
                playerProfile.UpdatedAt = DateTime.UtcNow;
            }

            var playerId = playerProfile?.PlayerId ?? _db.PlayerProfiles.Local
                .Where(pp => pp.UserId == user.UserId)
                .Select(pp => pp.PlayerId)
                .FirstOrDefault();

            if (playerId != Guid.Empty)
            {
                var currentPlayerTeams = await _db.PlayerTeams
                    .Where(pt => pt.PlayerId == playerId && pt.IsCurrent)
                    .ToListAsync();

                foreach (var playerTeam in currentPlayerTeams)
                {
                    playerTeam.IsCurrent = false;
                    playerTeam.LeftDate = DateOnly.FromDateTime(DateTime.UtcNow);
                    playerTeam.UpdatedAt = DateTime.UtcNow;
                }

                _db.PlayerTeams.Add(new PlayerTeam
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    TeamId = team.TeamId,
                    JoinedDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    IsCurrent = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
    }

    private async Task EnsurePlayerConstraintsAsync(Guid userId, RoleNameType incomingRole)
    {
        var isActivePlayer = await _db.TeamMemberships.AnyAsync(tm =>
            tm.UserId == userId &&
            tm.Role == RoleNameType.Player &&
            tm.Status == MembershipStatus.Active);

        if (isActivePlayer)
            throw new InvalidOperationException("Players cannot hold multiple active club or team memberships.");

        if (incomingRole == RoleNameType.Player)
        {
            var hasOwnedClub = await _db.Clubs.AnyAsync(c => c.CreatedBy == userId && c.DeletedAt == null);
            var hasActiveClubMembership = await _db.ClubMemberships.AnyAsync(cm =>
                cm.UserId == userId &&
                cm.Status == MembershipStatus.Active);
            var hasActiveTeamMembership = await _db.TeamMemberships.AnyAsync(tm =>
                tm.UserId == userId &&
                tm.Status == MembershipStatus.Active);

            if (hasOwnedClub || hasActiveClubMembership || hasActiveTeamMembership)
                throw new InvalidOperationException("A player cannot have multiple roles, clubs, or teams.");
        }
    }

    private async Task EnsureTeamInvitationAuthorityAsync(Team team, Guid callerUserId)
    {
        var isAdmin = await IsAdminAsync(callerUserId);
        var isClubManager = team.ClubId.HasValue
            && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId);
        var isTeamManager = await IsTeamManagerAsync(team.TeamId, callerUserId);
        var isTeamCreator = team.CreatedBy == callerUserId;

        if (!isAdmin && !isClubManager && !isTeamManager && !isTeamCreator)
            throw new UnauthorizedAccessException("Only a team manager, club manager, or admin can manage team invitations.");
    }

    private Task<bool> IsTeamManagerAsync(Guid teamId, Guid userId)
    {
        return _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == teamId &&
            tm.UserId == userId &&
            tm.Role == RoleNameType.TeamManager &&
            tm.Status == MembershipStatus.Active);
    }

    private Task<bool> IsAdminAsync(Guid userId)
    {
        return _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);
    }

    private static string GenerateInvitationToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static InvitationDto MapInvitation(Invitation invitation)
    {
        return new InvitationDto
        {
            InvitationId = invitation.InvitationId,
            Token = invitation.Token,
            Email = invitation.Email,
            Role = invitation.Role.ToString(),
            ClubName = invitation.Club.Name,
            TeamName = invitation.Team?.TeamName,
            PlayerPosition = invitation.PlayerPosition,
            JerseyNumber = invitation.JerseyNumber,
            InviterName = invitation.InvitedByUser.Name,
            Status = invitation.Status.ToString(),
            ExpiresAt = invitation.ExpiresAt,
            CreatedAt = invitation.CreatedAt
        };
    }

    private async Task EnsurePlayerFitsTeamCategoryAsync(Guid teamId, DateOnly dob)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT tc.min_age, tc.max_age
                FROM public.team t
                JOIN public.team_category tc ON tc.category_id = t.category_id
                WHERE t.team_id = @teamId
                LIMIT 1
                """;

            var teamIdParameter = command.CreateParameter();
            teamIdParameter.ParameterName = "@teamId";
            teamIdParameter.Value = teamId;
            command.Parameters.Add(teamIdParameter);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("Team category not found.");

            var minAge = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
            var maxAge = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            var age = CalculateAge(dob, DateOnly.FromDateTime(DateTime.UtcNow));

            if (minAge.HasValue && age < minAge.Value)
                throw new InvalidOperationException($"Player age {age} is below the category minimum age of {minAge.Value}.");

            if (maxAge.HasValue && age > maxAge.Value)
                throw new InvalidOperationException($"Player age {age} is above the category maximum age of {maxAge.Value}.");
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private async Task<bool> DenyInvitationForEmailAsync(string token, string email)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT public.deny_invitation_for_email(@token, @email)";

            var tokenParameter = command.CreateParameter();
            tokenParameter.ParameterName = "@token";
            tokenParameter.Value = token;
            command.Parameters.Add(tokenParameter);

            var emailParameter = command.CreateParameter();
            emailParameter.ParameterName = "@email";
            emailParameter.Value = email;
            command.Parameters.Add(emailParameter);

            var result = await command.ExecuteScalarAsync();
            return result is bool denied && denied;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static int CalculateAge(DateOnly dob, DateOnly today)
    {
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age))
            age--;

        return age;
    }
}
