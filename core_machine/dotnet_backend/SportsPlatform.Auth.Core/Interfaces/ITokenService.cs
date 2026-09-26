using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(
        User user,
        IEnumerable<string> roles,
        IEnumerable<UserClubInfoDto> clubs,
        IEnumerable<UserTeamInfoDto> teams,
        bool isAdmin);
    Task<RefreshToken> GenerateRefreshTokenAsync(Guid userId);
    Task<RefreshToken?> ValidateRefreshTokenAsync(string token);
    Task RevokeRefreshTokenAsync(string token);
    Task RevokeAllUserTokensAsync(Guid userId);
}
