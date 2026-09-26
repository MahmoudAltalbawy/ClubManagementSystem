using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IFitnessService
{
    Task<FitnessRecordDto> CreateFitnessRecordAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId, CreateFitnessRecordRequest request);
    Task<List<FitnessRecordDto>> GetPlayerFitnessRecordsAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId);
    Task<List<FitnessRecordDto>> GetMyFitnessRecordsAsync(Guid callerUserId);
}
