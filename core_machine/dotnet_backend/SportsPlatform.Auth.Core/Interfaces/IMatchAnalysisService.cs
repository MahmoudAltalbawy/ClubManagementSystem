using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IMatchAnalysisService
{
    Task<List<MatchAnalysisReportDto>> GetReportsAsync(Guid callerUserId);
    Task<MatchAnalysisReportDto> GetReportAsync(Guid reportId, Guid callerUserId);
    Task<MatchAnalysisSummaryDto> GetSummaryAsync(Guid callerUserId);
}
