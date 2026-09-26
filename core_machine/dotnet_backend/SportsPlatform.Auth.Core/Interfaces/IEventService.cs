using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IEventService
{
    Task<SeasonDto> CreateSeasonAsync(Guid callerUserId, CreateSeasonRequest request);
    Task<SeasonDto> CreateSeasonAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateSeasonRequest request);
    Task<List<SeasonDto>> GetSeasonsAsync(Guid callerUserId);
    Task<List<SeasonDto>> GetTeamSeasonsAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<SeasonDto?> GetCurrentSeasonAsync(Guid callerUserId);
    Task<SeasonDto?> GetCurrentSeasonAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<EventDto> CreateEventAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateEventRequest request);
    Task<List<EventDto>> GetTeamEventsAsync(Guid clubId, Guid teamId, Guid callerUserId, DateTime? from, DateTime? to);
    Task<EventDto> GetEventAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId);
    Task<EventDto> UpdateEventAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, UpdateEventRequest request);
    Task DeleteEventAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId);
    Task<EventDto> CancelEventInstanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, CancelEventInstanceRequest request);
    Task<EventDto> RescheduleEventInstanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, RescheduleEventInstanceRequest request);
}
