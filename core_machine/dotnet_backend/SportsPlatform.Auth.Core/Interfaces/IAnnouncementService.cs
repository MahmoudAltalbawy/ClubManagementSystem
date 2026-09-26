using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IAnnouncementService
{
    Task<AnnouncementDto> CreateAnnouncementAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateAnnouncementRequest request, Stream? imageStream = null, string? imageFileName = null);
    Task<AnnouncementDto> UpdateAnnouncementAsync(Guid clubId, Guid teamId, Guid announcementId, Guid callerUserId, UpdateAnnouncementRequest request, Stream? imageStream = null, string? imageFileName = null);
    Task<List<AnnouncementDto>> GetTeamAnnouncementsAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task DeleteAnnouncementAsync(Guid clubId, Guid teamId, Guid announcementId, Guid callerUserId);
}
