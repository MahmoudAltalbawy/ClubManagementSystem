using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IAttendanceService
{
    Task<List<AttendanceDto>> RecordAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, RecordAttendanceRequest request);
    Task<List<AttendanceDto>> GetEventAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, DateOnly? instanceDate);
    Task<AttendanceDto> UpdateAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid playerUserId, Guid callerUserId, UpdateAttendanceRequest request);
    Task<AttendanceDto?> GetMyAttendanceAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId, DateOnly? instanceDate);
}
