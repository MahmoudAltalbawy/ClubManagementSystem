using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface ISearchService
{
    Task<SearchResponseDto> SearchAsync(Guid callerUserId, string query, string type, int page, int pageSize, CancellationToken cancellationToken = default);
}
