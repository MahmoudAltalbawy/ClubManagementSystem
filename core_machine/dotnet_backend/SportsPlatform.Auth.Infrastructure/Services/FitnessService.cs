using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class FitnessService : IFitnessService
{
    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;

    public FitnessService(AppDbContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public async Task<FitnessRecordDto> CreateFitnessRecordAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId, CreateFitnessRecordRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanWriteFitnessAsync(team, callerUserId);

        var player = await GetActivePlayerOnTeamAsync(teamId, playerUserId);
        var now = DateTime.UtcNow;
        var customTestName = string.IsNullOrWhiteSpace(request.CustomTestName)
            ? null
            : request.CustomTestName.Trim();

        if (customTestName != null && !request.CustomTestResult.HasValue)
            throw new InvalidOperationException("Custom test result is required.");

        if (customTestName == null && request.CustomTestResult.HasValue)
            throw new InvalidOperationException("Custom test name is required.");

        var record = new FitnessRecord
        {
            FitnessId = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = player.PlayerId,
            FitnessUserId = callerUserId,
            TestDate = request.TestDate?.ToUniversalTime() ?? now,
            Height = request.Height,
            Weight = request.Weight,
            Bmi = request.Bmi,
            BodyFatPct = request.BodyFatPct,
            SpeedTestResult = request.SpeedTestResult,
            EnduranceScore = request.EnduranceScore,
            CustomTestName = customTestName,
            CustomTestResult = request.CustomTestResult,
            CreatedBy = callerUserId,
            UpdatedBy = callerUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.FitnessRecords.Add(record);
        await _db.SaveChangesAsync();

        await _notifications.CreateForUsersAsync([playerUserId], new CreateNotificationRequest
        {
            ActorUserId = callerUserId,
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = "FitnessRecordCreated",
            Priority = "Normal",
            DeliveryPolicy = "RealtimeIfConnected",
            Title = "New fitness record",
            Body = "Your fitness coach added a new record to your profile.",
            TargetType = "FitnessRecord",
            TargetId = record.FitnessId,
            TargetRoute = $"/profile/fitness/{record.FitnessId}"
        });

        return await BuildFitnessRecordDtoAsync(record.FitnessId);
    }

    public async Task<List<FitnessRecordDto>> GetPlayerFitnessRecordsAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanReadFitnessAsync(team, playerUserId, callerUserId);

        var player = await GetActivePlayerOnTeamAsync(teamId, playerUserId);
        var ids = await _db.FitnessRecords
            .Where(f => f.TeamId == teamId && f.PlayerId == player.PlayerId)
            .OrderByDescending(f => f.TestDate)
            .Select(f => f.FitnessId)
            .ToListAsync();

        var result = new List<FitnessRecordDto>(ids.Count);
        foreach (var id in ids)
            result.Add(await BuildFitnessRecordDtoAsync(id));

        return result;
    }

    public async Task<List<FitnessRecordDto>> GetMyFitnessRecordsAsync(Guid callerUserId)
    {
        var playerProfile = await _db.PlayerProfiles.FirstOrDefaultAsync(pp => pp.UserId == callerUserId)
            ?? throw new InvalidOperationException("Player profile not found.");

        var ids = await _db.FitnessRecords
            .Where(f => f.PlayerId == playerProfile.PlayerId)
            .OrderByDescending(f => f.TestDate)
            .Select(f => f.FitnessId)
            .ToListAsync();

        var result = new List<FitnessRecordDto>(ids.Count);
        foreach (var id in ids)
            result.Add(await BuildFitnessRecordDtoAsync(id));

        return result;
    }

    private async Task<FitnessRecordDto> BuildFitnessRecordDtoAsync(Guid fitnessId)
    {
        var record = await _db.FitnessRecords
            .Include(f => f.Player)
                .ThenInclude(p => p.User)
            .Include(f => f.FitnessUser)
            .FirstOrDefaultAsync(f => f.FitnessId == fitnessId)
            ?? throw new InvalidOperationException("Fitness record not found.");

        return new FitnessRecordDto
        {
            FitnessId = record.FitnessId,
            TeamId = record.TeamId,
            PlayerId = record.PlayerId,
            PlayerUserId = record.Player.UserId,
            PlayerName = record.Player.User.Name,
            FitnessUserId = record.FitnessUserId,
            FitnessUserName = record.FitnessUser?.Name,
            TestDate = record.TestDate,
            Height = record.Height,
            Weight = record.Weight,
            Bmi = record.Bmi,
            BodyFatPct = record.BodyFatPct,
            SpeedTestResult = record.SpeedTestResult,
            EnduranceScore = record.EnduranceScore,
            CustomTestName = record.CustomTestName,
            CustomTestResult = record.CustomTestResult
        };
    }

    private async Task<Team> GetTeamForClubAsync(Guid clubId, Guid teamId)
    {
        return await _db.Teams
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");
    }

    private async Task<PlayerProfile> GetActivePlayerOnTeamAsync(Guid teamId, Guid playerUserId)
    {
        var player = await _db.PlayerProfiles.FirstOrDefaultAsync(pp => pp.UserId == playerUserId)
            ?? throw new InvalidOperationException("Player profile not found.");

        var isActivePlayer = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == teamId &&
            tm.UserId == playerUserId &&
            tm.Role == RoleNameType.Player &&
            tm.Status == MembershipStatus.Active);

        if (!isActivePlayer)
            throw new InvalidOperationException("Player is not active on this team.");

        return player;
    }

    private async Task EnsureCanWriteFitnessAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        var isFitnessCoach = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Role == RoleNameType.FitnessCoach &&
            tm.Status == MembershipStatus.Active);

        if (!isFitnessCoach)
            throw new UnauthorizedAccessException("Only the fitness coach can create fitness records.");
    }

    private async Task EnsureCanReadFitnessAsync(Team team, Guid playerUserId, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        if (callerUserId == playerUserId)
            return;

        if (team.ClubId.HasValue &&
            await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId))
            return;

        var hasTeamMembership = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Status == MembershipStatus.Active &&
            tm.Role != RoleNameType.Player);

        if (!hasTeamMembership)
            throw new UnauthorizedAccessException("You do not have access to this fitness record.");
    }

    private Task<bool> IsAdminAsync(Guid userId)
    {
        return _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);
    }
}
