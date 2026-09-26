using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IMedicalService
{
    Task<MedicalRecordDto> CreateMedicalRecordAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId, CreateMedicalRecordRequest request);
    Task<MedicalRecordDto> UpdateMedicalRecordAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId, UpdateMedicalRecordRequest request);
    Task<List<MedicalRecordDto>> GetPlayerMedicalRecordsAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId);
    Task<List<MedicalRecordDto>> GetMyMedicalRecordsAsync(Guid callerUserId);
    Task<MedicalRecordDto> UpdateMedicalClearanceAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId, UpdateMedicalClearanceRequest request);
    Task<MedicalDocumentRequestDto> RequestMedicalDocumentAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId, RequestMedicalDocumentRequest request);
    Task<MedicalDocumentRequestDto> UploadMedicalDocumentAsync(Guid requestId, Guid callerUserId, Stream fileStream, string fileName, string contentType, long fileSizeBytes);
    Task<MedicalDocumentDownloadDto> GetMedicalDocumentDownloadAsync(Guid requestId, Guid callerUserId);
    Task DeleteMedicalRecordAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId);
}
