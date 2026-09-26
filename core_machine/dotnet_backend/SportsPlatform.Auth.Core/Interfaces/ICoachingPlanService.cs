using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface ICoachingPlanService
{
    Task<PlanDto> CreatePlanAsync(Guid clubId, Guid teamId, Guid callerUserId, CreatePlanRequest request);
    Task<List<PlanDto>> GetTeamPlansAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<PlanDto> GetPlanAsync(Guid clubId, Guid teamId, Guid planId, Guid callerUserId);
    Task<PlanDto> UpdatePlanAsync(Guid clubId, Guid teamId, Guid planId, Guid callerUserId, UpdatePlanRequest request);
    Task DeletePlanAsync(Guid clubId, Guid teamId, Guid planId, Guid callerUserId);
    Task<LineupDto> CreateLineupAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateLineupRequest request);
    Task<List<LineupDto>> GetTeamLineupsAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task<LineupDto> GetLineupAsync(Guid clubId, Guid teamId, Guid lineupId, Guid callerUserId);
    Task<LineupDto> UpdateLineupAsync(Guid clubId, Guid teamId, Guid lineupId, Guid callerUserId, UpdateLineupRequest request);
    Task DeleteLineupAsync(Guid clubId, Guid teamId, Guid lineupId, Guid callerUserId);
}
