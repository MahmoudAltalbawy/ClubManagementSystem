using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface ITeamService
{
    Task<List<TeamCategoryDto>> GetTeamCategoriesAsync();
    Task<TeamDto> CreateTeamAsync(Guid clubId, Guid callerUserId, CreateTeamRequest request, Stream? imageStream = null, string? imageFileName = null);
    Task<List<TeamDto>> GetMyTeamsAsync(Guid callerUserId);
    Task<List<TeamDto>> GetClubTeamsAsync(Guid clubId, Guid callerUserId);
    Task<TeamDto> GetTeamAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task DeleteTeamAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<List<TeamMemberDto>> GetTeamMembersAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task RemoveTeamMemberAsync(Guid clubId, Guid teamId, Guid targetUserId, Guid callerUserId);
}
