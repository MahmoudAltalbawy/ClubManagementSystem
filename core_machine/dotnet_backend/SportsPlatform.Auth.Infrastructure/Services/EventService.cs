using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class EventService : IEventService
{
    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;

    public EventService(AppDbContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public async Task<SeasonDto> CreateSeasonAsync(Guid callerUserId, CreateSeasonRequest request)
    {
        await EnsureCanCreateSeasonAsync(callerUserId);
        ValidateSeasonDates(request.StartDate, request.EndDate);

        var exists = await _db.Seasons.AnyAsync(s => s.TeamId == null && s.Label == request.Label.Trim());
        if (exists)
            throw new InvalidOperationException("A season with this label already exists.");

        if (request.IsCurrent)
        {
            var currentSeasons = await _db.Seasons.Where(s => s.TeamId == null && s.IsCurrent).ToListAsync();
            foreach (var season in currentSeasons)
                season.IsCurrent = false;
        }

        var now = DateTime.UtcNow;
        var seasonEntity = new Season
        {
            SeasonId = Guid.NewGuid(),
            CreatedBy = callerUserId,
            Label = request.Label.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IsCurrent = request.IsCurrent,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Seasons.Add(seasonEntity);
        await _db.SaveChangesAsync();

        return MapSeasonDto(seasonEntity);
    }

    public async Task<SeasonDto> CreateSeasonAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateSeasonRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanManageTeamAsync(team, callerUserId);
        ValidateSeasonDates(request.StartDate, request.EndDate);

        var label = request.Label.Trim();
        var exists = await _db.Seasons.AnyAsync(s => s.TeamId == team.TeamId && s.Label == label);
        if (exists)
            throw new InvalidOperationException("A season with this label already exists for this team.");

        if (request.IsCurrent)
        {
            var currentSeasons = await _db.Seasons
                .Where(s => s.TeamId == team.TeamId && s.IsCurrent)
                .ToListAsync();

            foreach (var season in currentSeasons)
                season.IsCurrent = false;
        }

        var now = DateTime.UtcNow;
        var seasonEntity = new Season
        {
            SeasonId = Guid.NewGuid(),
            TeamId = team.TeamId,
            CreatedBy = callerUserId,
            Label = label,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IsCurrent = request.IsCurrent,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Seasons.Add(seasonEntity);
        await _db.SaveChangesAsync();

        return MapSeasonDto(seasonEntity);
    }

    public async Task<List<SeasonDto>> GetSeasonsAsync(Guid callerUserId)
    {
        await EnsureAuthenticatedAsync(callerUserId);

        return await _db.Seasons
            .Include(s => s.Team)
            .Where(s => s.TeamId == null)
            .OrderByDescending(s => s.IsCurrent)
            .ThenByDescending(s => s.StartDate)
            .Select(s => new SeasonDto
            {
                SeasonId = s.SeasonId,
                TeamId = s.TeamId,
                TeamName = s.Team == null ? null : s.Team.TeamName,
                CreatedBy = s.CreatedBy,
                Label = s.Label,
                StartDate = s.StartDate,
                EndDate = s.EndDate,
                IsCurrent = s.IsCurrent
            })
            .ToListAsync();
    }

    public async Task<List<SeasonDto>> GetTeamSeasonsAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        return await _db.Seasons
            .Where(s => s.TeamId == team.TeamId)
            .OrderByDescending(s => s.IsCurrent)
            .ThenByDescending(s => s.StartDate)
            .Select(s => new SeasonDto
            {
                SeasonId = s.SeasonId,
                TeamId = s.TeamId,
                TeamName = team.TeamName,
                CreatedBy = s.CreatedBy,
                Label = s.Label,
                StartDate = s.StartDate,
                EndDate = s.EndDate,
                IsCurrent = s.IsCurrent
            })
            .ToListAsync();
    }

    public async Task<SeasonDto?> GetCurrentSeasonAsync(Guid callerUserId)
    {
        await EnsureAuthenticatedAsync(callerUserId);

        return await _db.Seasons
            .Where(s => s.TeamId == null && s.IsCurrent)
            .Select(s => new SeasonDto
            {
                SeasonId = s.SeasonId,
                TeamId = s.TeamId,
                CreatedBy = s.CreatedBy,
                Label = s.Label,
                StartDate = s.StartDate,
                EndDate = s.EndDate,
                IsCurrent = s.IsCurrent
            })
            .FirstOrDefaultAsync();
    }

    public async Task<SeasonDto?> GetCurrentSeasonAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var currentSeason = await _db.Seasons
            .FirstOrDefaultAsync(s => s.TeamId == team.TeamId && s.IsCurrent);

        if (currentSeason == null && await CanManageTeamAsync(team, callerUserId))
        {
            currentSeason = CreateDefaultTeamSeason(team.TeamId, callerUserId, DateTime.UtcNow);
            _db.Seasons.Add(currentSeason);
            await _db.SaveChangesAsync();
        }

        return currentSeason == null ? null : MapSeasonDto(currentSeason, team.TeamName);
    }

    public async Task<EventDto> CreateEventAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateEventRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanManageTeamAsync(team, callerUserId);
        var season = await ResolveSeasonForEventAsync(request.SeasonId, team, callerUserId, request.StartAt, request.RecurrenceEndDate);
        ValidateEventWindow(request.StartAt, request.EndAt);
        ValidateRecurrence(request.EventType, request.RecurrenceRule, request.RecurrenceEndDate);

        var now = DateTime.UtcNow;
        var eventEntity = new Event
        {
            EventId = Guid.NewGuid(),
            TeamId = team.TeamId,
            SeasonId = season.SeasonId,
            CreatedBy = callerUserId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            Location = request.Location?.Trim(),
            LocationLatitude = request.LocationLatitude,
            LocationLongitude = request.LocationLongitude,
            StartAt = EnsureUtc(request.StartAt),
            EndAt = EnsureUtcNullable(request.EndAt),
            EventType = request.EventType,
            Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone.Trim(),
            RecurrenceRule = request.RecurrenceRule?.Trim(),
            RecurrenceEndDate = EnsureUtcNullable(request.RecurrenceEndDate),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Events.Add(eventEntity);
        await _db.SaveChangesAsync();

        await NotifyTeamEventAsync(team, callerUserId, eventEntity.EventId, "EventCreated", "New event added", eventEntity.Title, eventEntity.StartAt);

        return await BuildEventDtoAsync(eventEntity.EventId);
    }

    public async Task<List<EventDto>> GetTeamEventsAsync(Guid clubId, Guid teamId, Guid callerUserId, DateTime? from, DateTime? to)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var query = _db.Events.Where(e => e.TeamId == teamId);

        if (from.HasValue)
        {
            var fromUtc = EnsureUtc(from.Value);
            query = query.Where(e => e.StartAt >= fromUtc || (e.RecurrenceEndDate != null && e.RecurrenceEndDate >= fromUtc));
        }

        if (to.HasValue)
        {
            var toUtc = EnsureUtc(to.Value);
            query = query.Where(e => e.StartAt <= toUtc || e.RecurrenceRule != null);
        }

        var eventIds = await query
            .OrderBy(e => e.StartAt)
            .Select(e => e.EventId)
            .ToListAsync();

        var result = new List<EventDto>(eventIds.Count);
        foreach (var eventId in eventIds)
            result.Add(await BuildEventDtoAsync(eventId));

        return result;
    }

    public async Task<EventDto> GetEventAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);
        await EnsureEventBelongsToTeamAsync(teamId, eventId);

        return await BuildEventDtoAsync(eventId);
    }

    public async Task<EventDto> UpdateEventAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, UpdateEventRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanManageTeamAsync(team, callerUserId);

        var eventEntity = await _db.Events
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found.");

        ValidateEventWindow(request.StartAt, request.EndAt);
        ValidateRecurrence(request.EventType, request.RecurrenceRule, request.RecurrenceEndDate);
        await ValidateSeasonForEventAsync(eventEntity.SeasonId, eventEntity.TeamId, request.StartAt, request.RecurrenceEndDate);

        eventEntity.Title = request.Title.Trim();
        eventEntity.Description = request.Description?.Trim();
        eventEntity.Location = request.Location?.Trim();
        eventEntity.LocationLatitude = request.LocationLatitude;
        eventEntity.LocationLongitude = request.LocationLongitude;
        eventEntity.StartAt = EnsureUtc(request.StartAt);
        eventEntity.EndAt = EnsureUtcNullable(request.EndAt);
        eventEntity.EventType = request.EventType;
        eventEntity.Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone.Trim();
        eventEntity.RecurrenceRule = request.RecurrenceRule?.Trim();
        eventEntity.RecurrenceEndDate = EnsureUtcNullable(request.RecurrenceEndDate);
        eventEntity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await NotifyTeamEventAsync(team, callerUserId, eventId, "EventUpdated", "Event updated", eventEntity.Title, eventEntity.StartAt);
        return await BuildEventDtoAsync(eventId);
    }

    public async Task DeleteEventAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanManageTeamAsync(team, callerUserId);

        var eventEntity = await _db.Events
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.TeamId == teamId && e.DeletedAt == null)
            ?? throw new InvalidOperationException("Event not found.");

        eventEntity.DeletedAt = DateTime.UtcNow;
        eventEntity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await NotifyTeamEventAsync(team, callerUserId, eventId, "EventCancelled", "Event cancelled", eventEntity.Title, eventEntity.StartAt, critical: IsNearTerm(eventEntity.StartAt));
    }

    public async Task<EventDto> CancelEventInstanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, CancelEventInstanceRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanManageTeamAsync(team, callerUserId);

        var eventEntity = await _db.Events
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found.");

        var exception = eventEntity.Exceptions.FirstOrDefault(ex => ex.OriginalDate == request.OriginalDate);
        var now = DateTime.UtcNow;

        if (exception == null)
        {
            _db.EventExceptions.Add(new EventException
            {
                EventExceptionId = Guid.NewGuid(),
                EventId = eventId,
                OriginalDate = request.OriginalDate,
                IsCancelled = true,
                Notes = request.Notes?.Trim(),
                CreatedBy = callerUserId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            exception.IsCancelled = true;
            exception.NewStartAt = null;
            exception.NewEndAt = null;
            exception.Notes = request.Notes?.Trim();
            exception.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
        var originalDateUtc = request.OriginalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await NotifyTeamEventAsync(team, callerUserId, eventId, "EventInstanceCancelled", "Event instance cancelled", eventEntity.Title, originalDateUtc, critical: IsNearTerm(originalDateUtc));
        return await BuildEventDtoAsync(eventId);
    }

    public async Task<EventDto> RescheduleEventInstanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, RescheduleEventInstanceRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanManageTeamAsync(team, callerUserId);

        var eventEntity = await _db.Events
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.TeamId == teamId)
            ?? throw new InvalidOperationException("Event not found.");

        ValidateEventWindow(request.NewStartAt, request.NewEndAt);

        var exception = eventEntity.Exceptions.FirstOrDefault(ex => ex.OriginalDate == request.OriginalDate);
        var now = DateTime.UtcNow;

        if (exception == null)
        {
            _db.EventExceptions.Add(new EventException
            {
                EventExceptionId = Guid.NewGuid(),
                EventId = eventId,
                OriginalDate = request.OriginalDate,
                NewStartAt = EnsureUtc(request.NewStartAt),
                NewEndAt = EnsureUtcNullable(request.NewEndAt),
                IsCancelled = false,
                Notes = request.Notes?.Trim(),
                CreatedBy = callerUserId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            exception.NewStartAt = EnsureUtc(request.NewStartAt);
            exception.NewEndAt = EnsureUtcNullable(request.NewEndAt);
            exception.IsCancelled = false;
            exception.Notes = request.Notes?.Trim();
            exception.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
        await NotifyTeamEventAsync(team, callerUserId, eventId, "EventInstanceRescheduled", "Event rescheduled", eventEntity.Title, request.NewStartAt, critical: IsNearTerm(request.NewStartAt));
        return await BuildEventDtoAsync(eventId);
    }

    private async Task<EventDto> BuildEventDtoAsync(Guid eventId)
    {
        var eventEntity = await _db.Events
            .Include(e => e.Team)
            .Include(e => e.Season)
            .Include(e => e.Creator)
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.EventId == eventId)
            ?? throw new InvalidOperationException("Event not found.");

        return new EventDto
        {
            EventId = eventEntity.EventId,
            TeamId = eventEntity.TeamId,
            SeasonId = eventEntity.SeasonId,
            SeasonLabel = eventEntity.Season.Label,
            TeamName = eventEntity.Team.TeamName,
            Title = eventEntity.Title,
            EventType = eventEntity.EventType.ToString(),
            StartAt = eventEntity.StartAt,
            EndAt = eventEntity.EndAt,
            Timezone = eventEntity.Timezone,
            Location = eventEntity.Location,
            LocationLatitude = eventEntity.LocationLatitude,
            LocationLongitude = eventEntity.LocationLongitude,
            Description = eventEntity.Description,
            RecurrenceRule = eventEntity.RecurrenceRule,
            RecurrenceEndDate = eventEntity.RecurrenceEndDate,
            CreatorName = eventEntity.Creator.Name,
            Exceptions = eventEntity.Exceptions
                .OrderBy(ex => ex.OriginalDate)
                .Select(ex => new EventExceptionDto
                {
                    EventExceptionId = ex.EventExceptionId,
                    OriginalDate = ex.OriginalDate,
                    NewStartAt = ex.NewStartAt,
                    NewEndAt = ex.NewEndAt,
                    IsCancelled = ex.IsCancelled,
                    Notes = ex.Notes
                })
                .ToList()
        };
    }

    private async Task<Team> GetTeamForClubAsync(Guid clubId, Guid teamId)
    {
        return await _db.Teams
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");
    }

    private async Task EnsureEventBelongsToTeamAsync(Guid teamId, Guid eventId)
    {
        var exists = await _db.Events.AnyAsync(e => e.EventId == eventId && e.TeamId == teamId);
        if (!exists)
            throw new InvalidOperationException("Event not found.");
    }

    private async Task ValidateSeasonForEventAsync(Guid seasonId, Guid teamId, DateTime startAt, DateTime? recurrenceEndDate)
    {
        var season = await _db.Seasons.FirstOrDefaultAsync(s => s.SeasonId == seasonId)
            ?? throw new InvalidOperationException("Season not found.");

        ValidateSeasonForEvent(season, teamId, startAt, recurrenceEndDate);
    }

    private async Task<Season> ResolveSeasonForEventAsync(Guid requestedSeasonId, Team team, Guid callerUserId, DateTime startAt, DateTime? recurrenceEndDate)
    {
        Season? season;

        if (requestedSeasonId == Guid.Empty)
        {
            season = await _db.Seasons
                .FirstOrDefaultAsync(s => s.TeamId == team.TeamId && s.IsCurrent);

            if (season == null)
            {
                season = CreateDefaultTeamSeason(team.TeamId, callerUserId, DateTime.UtcNow);
                _db.Seasons.Add(season);
            }
        }
        else
        {
            season = await _db.Seasons.FirstOrDefaultAsync(s => s.SeasonId == requestedSeasonId)
                ?? throw new InvalidOperationException("Season not found.");
        }

        ValidateSeasonForEvent(season, team.TeamId, startAt, recurrenceEndDate);
        return season;
    }

    private static void ValidateSeasonForEvent(Season season, Guid teamId, DateTime startAt, DateTime? recurrenceEndDate)
    {
        if (season.TeamId.HasValue && season.TeamId.Value != teamId)
            throw new InvalidOperationException("Season does not belong to this team.");

        var eventDate = DateOnly.FromDateTime(startAt.ToUniversalTime());
        if (eventDate < season.StartDate || eventDate > season.EndDate)
            throw new InvalidOperationException("Event start date must fall within the selected season.");

        if (recurrenceEndDate.HasValue)
        {
            var recurrenceDate = DateOnly.FromDateTime(recurrenceEndDate.Value.ToUniversalTime());
            if (recurrenceDate > season.EndDate)
                throw new InvalidOperationException("Recurrence end date cannot exceed the selected season end date.");
        }
    }

    private static void ValidateSeasonDates(DateOnly startDate, DateOnly endDate)
    {
        if (endDate <= startDate)
            throw new InvalidOperationException("Season end date must be after the start date.");
    }

    private static void ValidateEventWindow(DateTime startAt, DateTime? endAt)
    {
        if (endAt.HasValue && EnsureUtc(endAt.Value) <= EnsureUtc(startAt))
            throw new InvalidOperationException("Event end time must be after the start time.");
    }

    private static void ValidateRecurrence(EventType eventType, string? recurrenceRule, DateTime? recurrenceEndDate)
    {
        if (string.IsNullOrWhiteSpace(recurrenceRule))
            return;

        if (eventType != EventType.Training)
            throw new InvalidOperationException("Recurring schedules are only supported for training events.");

        if (!recurrenceEndDate.HasValue)
            throw new InvalidOperationException("Recurring training events require a recurrence end date.");
    }

    private async Task EnsureCanManageTeamAsync(Team team, Guid callerUserId)
    {
        if (!await CanManageTeamAsync(team, callerUserId))
            throw new UnauthorizedAccessException("You do not have permission to manage this team.");
    }

    private async Task<bool> CanManageTeamAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return true;

        if (team.ClubId.HasValue &&
            await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId))
            return true;

        return await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Role == RoleNameType.TeamManager &&
            tm.Status == MembershipStatus.Active);
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

    private async Task EnsureCanCreateSeasonAsync(Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        var isClubOwner = await _db.Clubs.AnyAsync(c => c.CreatedBy == callerUserId && c.DeletedAt == null);
        if (isClubOwner)
            return;

        var isTeamManager = await _db.TeamMemberships.AnyAsync(tm =>
            tm.UserId == callerUserId &&
            tm.Role == Core.Enums.RoleNameType.TeamManager &&
            tm.Status == Core.Enums.MembershipStatus.Active);

        if (isTeamManager)
            return;

        throw new UnauthorizedAccessException("Only club managers, team managers, or admins can create seasons.");
    }

    private async Task EnsureAdminAsync(Guid callerUserId)
    {
        if (!await IsAdminAsync(callerUserId))
            throw new UnauthorizedAccessException("Only admins can perform this action.");
    }

    private async Task EnsureAuthenticatedAsync(Guid callerUserId)
    {
        var exists = await _db.Users.AnyAsync(u => u.UserId == callerUserId);
        if (!exists)
            throw new UnauthorizedAccessException("User not found.");
    }

    private Task<bool> IsAdminAsync(Guid userId)
    {
        return _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private static DateTime? EnsureUtcNullable(DateTime? value)
    {
        return value.HasValue ? EnsureUtc(value.Value) : null;
    }

    private static Season CreateDefaultTeamSeason(Guid teamId, Guid createdBy, DateTime nowUtc)
    {
        var startYear = nowUtc.Month >= 7 ? nowUtc.Year : nowUtc.Year - 1;
        var startDate = new DateOnly(startYear, 7, 1);
        var endDate = new DateOnly(startYear + 1, 6, 30);

        return new Season
        {
            SeasonId = Guid.NewGuid(),
            TeamId = teamId,
            CreatedBy = createdBy,
            Label = $"{startYear}/{startYear + 1}",
            StartDate = startDate,
            EndDate = endDate,
            IsCurrent = true,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };
    }

    private static SeasonDto MapSeasonDto(Season season, string? teamName = null)
    {
        return new SeasonDto
        {
            SeasonId = season.SeasonId,
            TeamId = season.TeamId,
            TeamName = teamName ?? season.Team?.TeamName,
            CreatedBy = season.CreatedBy,
            Label = season.Label,
            StartDate = season.StartDate,
            EndDate = season.EndDate,
            IsCurrent = season.IsCurrent
        };
    }

    private Task NotifyTeamEventAsync(Team team, Guid actorUserId, Guid eventId, string type, string title, string eventTitle, DateTime eventDate, bool critical = false)
    {
        return _notifications.CreateForTeamAsync(team.TeamId, actorUserId, new CreateNotificationRequest
        {
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = type,
            Priority = critical ? "Critical" : "Normal",
            DeliveryPolicy = critical ? "EmailIfCriticalAndUnread" : "RealtimeIfConnected",
            Title = title,
            Body = $"{eventTitle} - {eventDate:g}",
            TargetType = "Event",
            TargetId = eventId,
            TargetRoute = $"/teams/{team.TeamId}/events/{eventId}"
        });
    }

    private static bool IsNearTerm(DateTime value)
    {
        var utc = EnsureUtc(value);
        var now = DateTime.UtcNow;
        return utc >= now && utc <= now.AddHours(24);
    }
}
