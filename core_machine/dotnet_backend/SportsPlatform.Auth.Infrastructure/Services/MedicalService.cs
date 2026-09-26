using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class MedicalService : IMedicalService
{
    private const long MaxMedicalDocumentSizeBytes = 10 * 1024 * 1024;
    private const string UploadedDocumentStatus = "Uploaded";
    private const string PendingDocumentStatus = "Pending";

    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IFileStorageService _storage;

    public MedicalService(AppDbContext db, INotificationService notifications, IFileStorageService storage)
    {
        _db = db;
        _notifications = notifications;
        _storage = storage;
    }

    public async Task<MedicalRecordDto> CreateMedicalRecordAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId, CreateMedicalRecordRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanWriteMedicalAsync(team, callerUserId);

        var player = await GetActivePlayerOnTeamAsync(teamId, playerUserId);
        var now = DateTime.UtcNow;

        var record = new MedicalRecord
        {
            RecordId = Guid.NewGuid(),
            TeamId = teamId,
            PlayerId = player.PlayerId,
            DoctorUserId = callerUserId,
            RecordDate = request.RecordDate?.ToUniversalTime() ?? now,
            InjuryType = string.IsNullOrWhiteSpace(request.InjuryType) ? null : request.InjuryType.Trim(),
            Diagnosis = string.IsNullOrWhiteSpace(request.Diagnosis) ? null : request.Diagnosis.Trim(),
            ExpectedReturnDate = request.ExpectedReturnDate,
            RecoveryTips = string.IsNullOrWhiteSpace(request.RecoveryTips) ? null : request.RecoveryTips.Trim(),
            IsCleared = false,
            CreatedBy = callerUserId,
            UpdatedBy = callerUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.MedicalRecords.Add(record);
        await _db.SaveChangesAsync();

        await NotifyMedicalRecordChangedAsync(team, playerUserId, callerUserId, record.RecordId, "Medical record updated", "Your team doctor added a medical record to your profile.");

        return await BuildMedicalRecordDtoAsync(record.RecordId);
    }

    public async Task<MedicalRecordDto> UpdateMedicalRecordAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId, UpdateMedicalRecordRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanWriteMedicalAsync(team, callerUserId);

        var record = await _db.MedicalRecords.FirstOrDefaultAsync(m => m.RecordId == recordId && m.TeamId == teamId)
            ?? throw new InvalidOperationException("Medical record not found.");

        record.RecordDate = request.RecordDate?.ToUniversalTime() ?? record.RecordDate;
        record.InjuryType = string.IsNullOrWhiteSpace(request.InjuryType) ? null : request.InjuryType.Trim();
        record.Diagnosis = string.IsNullOrWhiteSpace(request.Diagnosis) ? null : request.Diagnosis.Trim();
        record.ExpectedReturnDate = request.ExpectedReturnDate;
        record.RecoveryTips = string.IsNullOrWhiteSpace(request.RecoveryTips) ? null : request.RecoveryTips.Trim();
        record.UpdatedBy = callerUserId;
        record.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        var playerUserId = await _db.PlayerProfiles
            .Where(p => p.PlayerId == record.PlayerId)
            .Select(p => p.UserId)
            .FirstAsync();
        await NotifyMedicalRecordChangedAsync(team, playerUserId, callerUserId, record.RecordId, "Medical record updated", "Your team doctor updated your medical record.");
        return await BuildMedicalRecordDtoAsync(record.RecordId);
    }

    public async Task<List<MedicalRecordDto>> GetPlayerMedicalRecordsAsync(Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanReadMedicalAsync(team, playerUserId, callerUserId);

        var player = await GetActivePlayerOnTeamAsync(teamId, playerUserId);
        var ids = await _db.MedicalRecords
            .Where(m => m.TeamId == teamId && m.PlayerId == player.PlayerId)
            .OrderByDescending(m => m.RecordDate)
            .Select(m => m.RecordId)
            .ToListAsync();

        var result = new List<MedicalRecordDto>(ids.Count);
        foreach (var id in ids)
            result.Add(await BuildMedicalRecordDtoAsync(id));

        return result;
    }

    public async Task<List<MedicalRecordDto>> GetMyMedicalRecordsAsync(Guid callerUserId)
    {
        var playerProfile = await _db.PlayerProfiles.FirstOrDefaultAsync(pp => pp.UserId == callerUserId)
            ?? throw new InvalidOperationException("Player profile not found.");

        var ids = await _db.MedicalRecords
            .Where(m => m.PlayerId == playerProfile.PlayerId)
            .OrderByDescending(m => m.RecordDate)
            .Select(m => m.RecordId)
            .ToListAsync();

        var result = new List<MedicalRecordDto>(ids.Count);
        foreach (var id in ids)
            result.Add(await BuildMedicalRecordDtoAsync(id));

        return result;
    }

    public async Task<MedicalRecordDto> UpdateMedicalClearanceAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId, UpdateMedicalClearanceRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanWriteMedicalAsync(team, callerUserId);

        var record = await _db.MedicalRecords.FirstOrDefaultAsync(m => m.RecordId == recordId && m.TeamId == teamId)
            ?? throw new InvalidOperationException("Medical record not found.");

        var now = DateTime.UtcNow;
        if (record.IsCleared && !request.IsCleared && record.UpdatedAt.AddDays(14) <= now)
            throw new InvalidOperationException("This injury was cleared more than two weeks ago and can no longer be marked as not cleared.");

        record.IsCleared = request.IsCleared;
        record.UpdatedBy = callerUserId;
        record.UpdatedAt = now;

        await _db.SaveChangesAsync();
        var playerUserId = await _db.PlayerProfiles
            .Where(p => p.PlayerId == record.PlayerId)
            .Select(p => p.UserId)
            .FirstAsync();
        await _notifications.CreateForUsersAsync([playerUserId], new CreateNotificationRequest
        {
            ActorUserId = callerUserId,
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = request.IsCleared ? "MedicalClearanceGranted" : "MedicalClearanceRevoked",
            Priority = "High",
            DeliveryPolicy = "RealtimeIfConnected",
            Title = request.IsCleared ? "You are cleared to play" : "Medical clearance updated",
            Body = request.IsCleared ? "Your doctor cleared you to return. Review your medical profile." : "Your doctor updated your clearance status.",
            TargetType = "MedicalRecord",
            TargetId = record.RecordId,
            TargetRoute = $"/profile/medical/{record.RecordId}"
        });
        return await BuildMedicalRecordDtoAsync(record.RecordId);
    }

    public async Task DeleteMedicalRecordAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanWriteMedicalAsync(team, callerUserId);

        var record = await _db.MedicalRecords
            .Include(m => m.DocumentRequests)
            .FirstOrDefaultAsync(m => m.RecordId == recordId && m.TeamId == teamId)
            ?? throw new InvalidOperationException("Medical record not found.");

        _db.MedicalDocumentRequests.RemoveRange(record.DocumentRequests);
        _db.MedicalRecords.Remove(record);
        await _db.SaveChangesAsync();
    }

    private async Task<MedicalRecordDto> BuildMedicalRecordDtoAsync(Guid recordId)
    {
        var record = await _db.MedicalRecords
            .Include(m => m.Player)
                .ThenInclude(p => p.User)
            .Include(m => m.DoctorUser)
            .Include(m => m.DocumentRequests)
                .ThenInclude(r => r.RequestedByUser)
            .Include(m => m.DocumentRequests)
                .ThenInclude(r => r.UploadedByUser)
            .FirstOrDefaultAsync(m => m.RecordId == recordId)
            ?? throw new InvalidOperationException("Medical record not found.");

        return new MedicalRecordDto
        {
            RecordId = record.RecordId,
            TeamId = record.TeamId,
            PlayerId = record.PlayerId,
            PlayerUserId = record.Player.UserId,
            PlayerName = record.Player.User.Name,
            DoctorUserId = record.DoctorUserId,
            DoctorName = record.DoctorUser?.Name,
            RecordDate = record.RecordDate,
            UpdatedAt = record.UpdatedAt,
            InjuryType = record.InjuryType,
            Diagnosis = record.Diagnosis,
            ExpectedReturnDate = record.ExpectedReturnDate,
            RecoveryTips = record.RecoveryTips,
            IsCleared = record.IsCleared,
            DocumentRequests = record.DocumentRequests
                .OrderByDescending(r => r.RequestedAt)
                .Select(MapDocumentRequest)
                .ToList()
        };
    }

    public async Task<MedicalDocumentRequestDto> RequestMedicalDocumentAsync(Guid clubId, Guid teamId, Guid recordId, Guid callerUserId, RequestMedicalDocumentRequest request)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanWriteMedicalAsync(team, callerUserId);

        var record = await _db.MedicalRecords.FirstOrDefaultAsync(m => m.RecordId == recordId && m.TeamId == teamId)
            ?? throw new InvalidOperationException("Medical record not found.");

        var documentName = request.DocumentName.Trim();
        if (string.IsNullOrWhiteSpace(documentName))
            throw new InvalidOperationException("Document name is required.");

        var now = DateTime.UtcNow;
        var documentRequest = new MedicalDocumentRequest
        {
            RequestId = Guid.NewGuid(),
            RecordId = record.RecordId,
            DocumentName = documentName,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            Status = PendingDocumentStatus,
            RequestedBy = callerUserId,
            RequestedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.MedicalDocumentRequests.Add(documentRequest);
        await _db.SaveChangesAsync();

        var playerUserId = await _db.MedicalRecords
            .Include(m => m.Player)
            .Where(m => m.RecordId == record.RecordId)
            .Select(m => m.Player.UserId)
            .FirstAsync();
        await _notifications.CreateForUsersAsync([playerUserId], new CreateNotificationRequest
        {
            ActorUserId = callerUserId,
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = "MedicalDocumentRequested",
            Priority = "Critical",
            DeliveryPolicy = "EmailIfCriticalAndUnread",
            Title = "Medical document requested",
            Body = $"Your team doctor requested: {documentName}.",
            TargetType = "MedicalDocumentRequest",
            TargetId = documentRequest.RequestId,
            TargetRoute = $"/profile/medical/document-requests/{documentRequest.RequestId}"
        });

        return await BuildMedicalDocumentRequestDtoAsync(documentRequest.RequestId);
    }

    public async Task<MedicalDocumentRequestDto> UploadMedicalDocumentAsync(
        Guid requestId,
        Guid callerUserId,
        Stream fileStream,
        string fileName,
        string contentType,
        long fileSizeBytes)
    {
        if (fileSizeBytes <= 0)
            throw new InvalidOperationException("A document file is required.");

        if (fileSizeBytes > MaxMedicalDocumentSizeBytes)
            throw new InvalidOperationException("Document file must be 10 MB or smaller.");

        var documentRequest = await _db.MedicalDocumentRequests
            .Include(r => r.Record)
                .ThenInclude(m => m.Player)
                    .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.RequestId == requestId)
            ?? throw new InvalidOperationException("Document request not found.");

        if (documentRequest.Record.Player.UserId != callerUserId)
            throw new UnauthorizedAccessException("Only the requested player can upload this document.");

        if (documentRequest.Status == UploadedDocumentStatus)
            throw new InvalidOperationException("A document has already been uploaded for this request.");

        var now = DateTime.UtcNow;
        var safeOriginalName = SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeOriginalName);
        var storedFileName = $"{requestId:N}_{Guid.NewGuid():N}{extension}";
        var fileUrl = await _storage.SaveFileAsync(fileStream, fileName, "medical-documents", contentType);

        documentRequest.Status = UploadedDocumentStatus;
        documentRequest.UploadedBy = callerUserId;
        documentRequest.OriginalFileName = safeOriginalName;
        documentRequest.StoredFileName = fileUrl;
        documentRequest.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        documentRequest.FileSizeBytes = fileSizeBytes;
        documentRequest.UploadedAt = now;
        documentRequest.UpdatedAt = now;

        await _db.SaveChangesAsync();
        await _notifications.CreateForUsersAsync([documentRequest.RequestedBy], new CreateNotificationRequest
        {
            ActorUserId = callerUserId,
            TeamId = documentRequest.Record.TeamId,
            Type = "MedicalDocumentUploaded",
            Priority = "High",
            DeliveryPolicy = "RealtimeIfConnected",
            Title = "Medical document uploaded",
            Body = $"{documentRequest.Record.Player.User.Name} uploaded a requested medical document.",
            TargetType = "MedicalDocumentRequest",
            TargetId = documentRequest.RequestId,
            TargetRoute = $"/medical/document-requests/{documentRequest.RequestId}"
        });
        return await BuildMedicalDocumentRequestDtoAsync(documentRequest.RequestId);
    }

    public async Task<MedicalDocumentDownloadDto> GetMedicalDocumentDownloadAsync(Guid requestId, Guid callerUserId)
    {
        var documentRequest = await _db.MedicalDocumentRequests
            .Include(r => r.Record)
                .ThenInclude(m => m.Team)
            .Include(r => r.Record)
                .ThenInclude(m => m.Player)
                    .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.RequestId == requestId)
            ?? throw new InvalidOperationException("Document request not found.");

        await EnsureCanReadMedicalAsync(documentRequest.Record.Team, documentRequest.Record.Player.UserId, callerUserId);

        if (documentRequest.StoredFileName == null)
            throw new InvalidOperationException("Document has not been uploaded yet.");

        var filePath = documentRequest.StoredFileName;

        return new MedicalDocumentDownloadDto
        {
            FilePath = filePath,
            FileName = documentRequest.OriginalFileName ?? documentRequest.StoredFileName,
            ContentType = documentRequest.ContentType ?? "application/octet-stream"
        };
    }

    private async Task<Team> GetTeamForClubAsync(Guid clubId, Guid teamId)
    {
        return await _db.Teams
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");
    }

    private async Task<PlayerProfile> GetActivePlayerOnTeamAsync(Guid teamId, Guid playerUserId)
    {
        var player = await _db.PlayerProfiles.FirstOrDefaultAsync(pp => pp.UserId == playerUserId)
            ?? throw new InvalidOperationException("Player profile not found.");

        var isActivePlayer = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == teamId &&
            tm.UserId == playerUserId &&
            tm.Role == RoleNameType.Player &&
            tm.Status == MembershipStatus.Active);

        if (!isActivePlayer)
            throw new InvalidOperationException("Player is not active on this team.");

        return player;
    }

    private async Task EnsureCanWriteMedicalAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        var isTeamDoctor = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Role == RoleNameType.TeamDoctor &&
            tm.Status == MembershipStatus.Active);

        if (!isTeamDoctor)
            throw new UnauthorizedAccessException("Only the team doctor can modify medical records.");
    }

    private async Task EnsureCanReadMedicalAsync(Team team, Guid playerUserId, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId))
            return;

        if (callerUserId == playerUserId)
            return;

        if (team.ClubId.HasValue &&
            await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId))
            return;

        var hasTeamMembership = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId &&
            tm.UserId == callerUserId &&
            tm.Status == MembershipStatus.Active &&
            tm.Role != RoleNameType.Player);

        if (!hasTeamMembership)
            throw new UnauthorizedAccessException("You do not have access to this medical record.");
    }

    private Task<bool> IsAdminAsync(Guid userId)
    {
        return _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);
    }

    private async Task NotifyMedicalRecordChangedAsync(Team team, Guid playerUserId, Guid callerUserId, Guid recordId, string title, string body)
    {
        await _notifications.CreateForUsersAsync([playerUserId], new CreateNotificationRequest
        {
            ActorUserId = callerUserId,
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = "MedicalRecordUpdated",
            Priority = "High",
            DeliveryPolicy = "RealtimeIfConnected",
            Title = title,
            Body = body,
            TargetType = "MedicalRecord",
            TargetId = recordId,
            TargetRoute = $"/profile/medical/{recordId}"
        });

        var staffIds = await _db.TeamMemberships
            .Where(tm => tm.TeamId == team.TeamId &&
                tm.UserId != callerUserId &&
                tm.UserId != playerUserId &&
                tm.Status == MembershipStatus.Active &&
                tm.Role != RoleNameType.Player)
            .Select(tm => tm.UserId)
            .Distinct()
            .ToListAsync();

        await _notifications.CreateForUsersAsync(staffIds, new CreateNotificationRequest
        {
            ActorUserId = callerUserId,
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = "PlayerInjuryStatusUpdated",
            Priority = "High",
            DeliveryPolicy = "RealtimeIfConnected",
            Title = "Player injury status updated",
            Body = "A player's medical status changed. Open the team medical area if you have permission.",
            TargetType = "MedicalRecord",
            TargetId = recordId,
            TargetRoute = $"/teams/{team.TeamId}/medical/{recordId}"
        });
    }

    private async Task<MedicalDocumentRequestDto> BuildMedicalDocumentRequestDtoAsync(Guid requestId)
    {
        var documentRequest = await _db.MedicalDocumentRequests
            .Include(r => r.RequestedByUser)
            .Include(r => r.UploadedByUser)
            .FirstOrDefaultAsync(r => r.RequestId == requestId)
            ?? throw new InvalidOperationException("Document request not found.");

        return MapDocumentRequest(documentRequest);
    }

    private static MedicalDocumentRequestDto MapDocumentRequest(MedicalDocumentRequest request)
    {
        return new MedicalDocumentRequestDto
        {
            RequestId = request.RequestId,
            RecordId = request.RecordId,
            DocumentName = request.DocumentName,
            Note = request.Note,
            Status = request.Status,
            RequestedByName = request.RequestedByUser?.Name,
            UploadedByName = request.UploadedByUser?.Name,
            OriginalFileName = request.OriginalFileName,
            ContentType = request.ContentType,
            FileSizeBytes = request.FileSizeBytes,
            RequestedAt = request.RequestedAt,
            UploadedAt = request.UploadedAt,
            DownloadUrl = request.Status == UploadedDocumentStatus
                ? $"/medical/document-requests/{request.RequestId}/download"
                : null
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            return "medical-document";

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(invalidChar, '_');

        return safeName;
    }
}
