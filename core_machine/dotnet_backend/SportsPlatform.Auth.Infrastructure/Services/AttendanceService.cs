using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class AttendanceService : IAttendanceService
{
    private readonly AppDbContext _db;

    public AttendanceService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<AttendanceDto>> RecordAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, RecordAttendanceRequest request)
    {
        if (request.Records.Count == 0)
            throw new InvalidOperationException("At least one attendance record is required.");

        var eventEntity = await GetEventForTeamAsync(clubId, teamId, eventId);
        await EnsureCanManageTeamAsync(eventEntity.Team, callerUserId);
        ValidateInstanceDate(eventEntity, request.InstanceDate);

        var duplicateUsers = request.Records
            .GroupBy(r => r.PlayerUserId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateUsers.Count > 0)
            throw new InvalidOperationException("Attendance request contains duplicate players.");

        foreach (var record in request.Records)
        {
            await UpsertAttendanceAsync(eventEntity, request.InstanceDate, record.PlayerUserId, record.Status, record.Notes, callerUserId);
        }

        await _db.SaveChangesAsync();
        return await GetAttendanceDtosAsync(eventId, request.InstanceDate);
    }

    public async Task<List<AttendanceDto>> GetEventAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, DateOnly? instanceDate)
    {
        var eventEntity = await GetEventForTeamAsync(clubId, teamId, eventId);
        await EnsureCanManageTeamAsync(eventEntity.Team, callerUserId);

        var resolvedDate = ResolveInstanceDate(eventEntity, instanceDate);
        return await GetAttendanceDtosAsync(eventId, resolvedDate);
    }

    public async Task<AttendanceDto> UpdateAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid playerUserId, Guid callerUserId, UpdateAttendanceRequest request)
    {
        var eventEntity = await GetEventForTeamAsync(clubId, teamId, eventId);
        await EnsureCanManageTeamAsync(eventEntity.Team, callerUserId);
        ValidateInstanceDate(eventEntity, request.InstanceDate);

        var attendance = await UpsertAttendanceAsync(eventEntity, request.InstanceDate, playerUserId, request.Status, request.Notes, callerUserId);
        await _db.SaveChangesAsync();

        return await BuildAttendanceDtoAsync(attendance.AttendanceId);
    }

    public async Task<AttendanceDto?> GetMyAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, DateOnly? instanceDate)
    {
        var eventEntity = await GetEventForTeamAsync(clubId, teamId, eventId);
        await EnsurePlayerCanViewOwnAttendanceAsync(eventEntity.Team, callerUserId);

        var resolvedDate = ResolveInstanceDate(eventEntity, instanceDate);
        var playerProfile = await _db.PlayerProfiles.FirstOrDefaultAsync(pp => pp.UserId == callerUserId)
            ?? throw new InvalidOperationException("Player profile not found.");

        var attendanceId = await _db.Attendances
            .Where(a => a.EventId == eventId && a.PlayerId == playerProfile.PlayerId && a.InstanceDate == resolvedDate)
            .Select(a => (Guid?)a.AttendanceId)
            .FirstOrDefaultAsync();

        if (!attendanceId.HasValue)
            return null;

        return await BuildAttendanceDtoAsync(attendanceId.Value);
    }

    private async Task<Attendance> UpsertAttendanceAsync(Event eventEntity, DateOnly instanceDate, Guid playerUserId, AttendanceStatus status, string? notes, Guid callerUserId)
    {
        var isActivePlayerOnTeam = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == eventEntity.TeamId &&
            tm.UserId == playerUserId &&
            tm.Role == RoleNameType.Player &&
            tm.Status == MembershipStatus.Active);

        if (!isActivePlayerOnTeam)
            throw new InvalidOperationException("Selected user is not an active player on this team.");

        var playerProfile = await EnsurePlayerProfileForTeamAsync(eventEntity.TeamId, playerUserId);

        var attendance = await _db.Attendances.FirstOrDefaultAsync(a =>
            a.EventId == eventEntity.EventId &&
            a.InstanceDate == instanceDate &&
            a.PlayerId == playerProfile.PlayerId);

        var now = DateTime.UtcNow;
        if (attendance == null)
        {
            attendance = new Attendance
            {
                AttendanceId = Guid.NewGuid(),
                EventId = eventEntity.EventId,
                InstanceDate = instanceDate,
                PlayerId = playerProfile.PlayerId,
                CreatedAt = now
            };

            _db.Attendances.Add(attendance);
        }

        attendance.Status = status;
        attendance.RecordedByUserId = callerUserId;
        attendance.RecordedAt = now;
        attendance.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        attendance.UpdatedAt = now;

        return attendance;
    }

    private async Task<PlayerProfile> EnsurePlayerProfileForTeamAsync(Guid teamId, Guid playerUserId)
    {
        var now = DateTime.UtcNow;
        var playerProfile = await _db.PlayerProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(pp => pp.UserId == playerUserId);

        if (playerProfile == null)
        {
            playerProfile = new PlayerProfile
            {
                PlayerId = Guid.NewGuid(),
                UserId = playerUserId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.PlayerProfiles.Add(playerProfile);
        }
        else
        {
            playerProfile.DeletedAt = null;
            playerProfile.UpdatedAt = now;
        }

        var hasCurrentPlayerTeam = await _db.PlayerTeams.AnyAsync(pt =>
            pt.PlayerId == playerProfile.PlayerId &&
            pt.TeamId == teamId &&
            pt.IsCurrent);

        if (!hasCurrentPlayerTeam)
        {
            _db.PlayerTeams.Add(new PlayerTeam
            {
                Id = Guid.NewGuid(),
                PlayerId = playerProfile.PlayerId,
                TeamId = teamId,
                JoinedDate = DateOnly.FromDateTime(now),
                IsCurrent = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        return playerProfile;
    }

    private async Task<List<AttendanceDto>> GetAttendanceDtosAsync(Guid eventId, DateOnly instanceDate)
    {
        var attendanceIds = await _db.Attendances
            .Where(a => a.EventId == eventId && a.InstanceDate == instanceDate)
            .OrderBy(a => a.Player.User.Name)
            .Select(a => a.AttendanceId)
            .ToListAsync();

        var result = new List<AttendanceDto>(attendanceIds.Count);
        foreach (var attendanceId in attendanceIds)
            result.Add(await BuildAttendanceDtoAsync(attendanceId));

        return result;
    }

    private async Task<AttendanceDto> BuildAttendanceDtoAsync(Guid attendanceId)
    {
        var attendance = await _db.Attendances
            .Include(a => a.Player)
                .ThenInclude(p => p.User)
            .Include(a => a.RecordedByUser)
            .FirstOrDefaultAsync(a => a.AttendanceId == attendanceId)
            ?? throw new InvalidOperationException("Attendance record not found.");

        return new AttendanceDto
        {
            AttendanceId = attendance.AttendanceId,
            EventId = attendance.EventId,
            InstanceDate = attendance.InstanceDate,
            PlayerId = attendance.PlayerId,
            PlayerUserId = attendance.Player.UserId,
            PlayerName = attendance.Player.User.Name,
            Status = attendance.Status.ToString(),
            RecordedByUserId = attendance.RecordedByUserId,
            RecordedByName = attendance.RecordedByUser?.Name,
            RecordedAt = attendance.RecordedAt,
            Notes = attendance.Notes
        };
    }

    private async Task<Event> GetEventForTeamAsync(Guid clubId, Guid teamId, Guid eventId)
    {
        return await _db.Events
            .Include(e => e.Team)
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.TeamId == teamId && e.Team.ClubId == clubId)
            ?? throw new InvalidOperationException("Event not found.");
    }

    private static DateOnly ResolveInstanceDate(Event eventEntity, DateOnly? instanceDate)
    {
        return instanceDate ?? DateOnly.FromDateTime(eventEntity.StartAt);
    }

    private static void ValidateInstanceDate(Event eventEntity, DateOnly instanceDate)
    {
        var startDate = DateOnly.FromDateTime(eventEntity.StartAt);
        if (string.IsNullOrWhiteSpace(eventEntity.RecurrenceRule))
        {
            if (instanceDate != startDate)
                throw new InvalidOperationException("One-off events only allow attendance on the event date.");

            return;
        }

        if (instanceDate < startDate)
            throw new InvalidOperationException("Attendance instance date cannot be before the event start date.");

        if (eventEntity.RecurrenceEndDate.HasValue && instanceDate > DateOnly.FromDateTime(eventEntity.RecurrenceEndDate.Value))
            throw new InvalidOperationException("Attendance instance date cannot exceed the recurrence end date.");
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
            throw new UnauthorizedAccessException("You do not have permission to manage attendance for this team.");
    }

    private async Task EnsurePlayerCanViewOwnAttendanceAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        var isClubManager = team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId);
        if (isClubManager)
            return;

        var isPlayer = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Role == RoleNameType.Player &&
            tm.Status == MembershipStatus.Active);

        if (!isPlayer)
            throw new UnauthorizedAccessException("You do not have access to this attendance record.");
    }

    private Task<bool> IsAdminAsync(Guid userId)
    {
        return _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);
    }
}
