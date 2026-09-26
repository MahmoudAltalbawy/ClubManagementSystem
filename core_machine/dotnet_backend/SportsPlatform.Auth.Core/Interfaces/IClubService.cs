using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IClubService
{
    Task<ClubDto> CreateClubAsync(Guid userId, CreateClubRequest request, Stream? logoStream = null, string? logoFileName = null);
    Task<ClubDto> UpdateClubLogoAsync(Guid clubId, Guid callerUserId, Stream logoStream, string logoFileName);
    Task<ClubDto> GetClubAsync(Guid clubId, Guid callerUserId);
    Task<List<ClubSummaryDto>> GetMyClubsAsync(Guid userId);
    Task DeleteClubAsync(Guid clubId, Guid callerUserId);
    Task<List<ClubMemberDto>> GetClubMembersAsync(Guid clubId, Guid callerUserId);
    Task RemoveClubMemberAsync(Guid clubId, Guid targetUserId, Guid callerUserId);
}
