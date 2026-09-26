using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IInvitationService
{
    Task<InvitationDto> CreateClubInvitationAsync(Guid clubId, CreateInvitationRequest request, Guid invitedBy);
    Task<InvitationDto> CreateTeamInvitationAsync(Guid clubId, Guid teamId, CreateInvitationRequest request, Guid invitedBy);
    Task<InvitationDto> GetInvitationAsync(string token);
    Task<List<InvitationDto>> GetMyPendingInvitationsAsync(Guid userId);
    Task<InvitationAcceptResultDto> AcceptInvitationAsync(string token, Guid acceptingUserId);
    Task DenyInvitationAsync(string token, Guid denyingUserId);
    Task<List<InvitationDto>> GetClubInvitationsAsync(Guid clubId, Guid callerUserId);
    Task<List<InvitationDto>> GetTeamInvitationsAsync(Guid clubId, Guid teamId, Guid callerUserId);
    Task CancelInvitationAsync(Guid invitationId, Guid callerUserId);
    Task CleanupFinalizedInvitationsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}
