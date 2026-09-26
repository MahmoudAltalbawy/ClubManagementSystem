using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IPlayerService
{
    Task<PlayerProfileDto> GetMyProfileAsync(Guid callerUserId);
    Task<List<PlayerProfileDto>> GetTeamPlayersAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<PlayerProfileDto> GetPlayerProfileAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId);
    Task<PlayerProfileDto> UpsertPlayerProfileAsync(
        Guid clubId,
        Guid teamId,
        Guid playerUserId,
        Guid callerUserId,
        UpsertPlayerProfileRequest request);
}
