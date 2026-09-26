using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IGameStatsService
{
    Task<MatchStatsDto> CreateStatsAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateMatchStatsRequest request);
    Task<StatsUploadPreviewDto> PreviewUploadAsync(Guid clubId, Guid teamId, Guid callerUserId, Guid eventId, string fileName, Stream fileContent);
    Task<TeamStatsAggregateDto> GetTeamAggregatesAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<List<MatchStatsSummaryDto>> GetMatchHistoryAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<MatchStatsDto> GetMatchStatsAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId);
    Task DeleteMatchStatsAsync(Guid clubId, Guid teamId, Guid eventId, Guid callerUserId);
    Task<PlayerStatsAggregateDto> GetPlayerAggregateAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId);
    Task<List<PlayerMatchStatsDto>> GetPlayerMatchHistoryAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId);

    // ── Basketball-specific ──
    Task<BasketballUploadPreviewDto> ExtractBasketballPdfAsync(Guid clubId, Guid teamId, Guid callerUserId, string fileName, Stream fileContent);
    Task<BasketballMatchStatsDto> CreateBasketballStatsAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateBasketballStatsRequest request);
    Task<BasketballMatchStatsDto> ConfirmBasketballUploadAsync(Guid clubId, Guid teamId, Guid callerUserId, ConfirmBasketballUploadRequest request);
    Task<BasketballTeamAggregateDto> GetBasketballAggregatesAsync(Guid clubId, Guid teamId, Guid callerUserId);
}
