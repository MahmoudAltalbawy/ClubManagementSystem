using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class NotificationService : INotificationService
{
    private const string EmailCriticalAndUnread = "EmailIfCriticalAndUnread";
    private const string CriticalPriority = "Critical";

    private readonly AppDbContext _db;
    private readonly INotificationRealtimePublisher _publisher;
    private readonly IRealtimeConnectionTracker _connectionTracker;
    private readonly IEmailService _emailService;

    public NotificationService(
        AppDbContext db,
        INotificationRealtimePublisher publisher,
        IRealtimeConnectionTracker connectionTracker,
        IEmailService emailService)
    {
        _db = db;
        _publisher = publisher;
        _connectionTracker = connectionTracker;
        _emailService = emailService;
    }

    public async Task<List<NotificationDto>> CreateForTeamAsync(
        Guid teamId,
        Guid actorUserId,
        CreateNotificationRequest request,
        bool excludeActor = true,
        CancellationToken cancellationToken = default)
    {
        var recipientIds = await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.Status == MembershipStatus.Active)
            .Where(tm => !excludeActor || tm.UserId != actorUserId)
            .Select(tm => tm.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        request.TeamId ??= teamId;
        request.ActorUserId ??= actorUserId;
        return await CreateForUsersAsync(recipientIds, request, cancellationToken);
    }

    public async Task<List<NotificationDto>> CreateForUsersAsync(
        IEnumerable<Guid> recipientUserIds,
        CreateNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var ids = recipientUserIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return new List<NotificationDto>();

        var existingRecipientIds = new HashSet<Guid>();
        if (!string.IsNullOrWhiteSpace(request.UniqueKey))
        {
            existingRecipientIds = (await _db.AppNotifications
                    .Where(n => n.UniqueKey != null && n.UniqueKey.StartsWith(request.UniqueKey) && ids.Contains(n.RecipientUserId))
                    .Select(n => n.RecipientUserId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        var now = DateTime.UtcNow;
        var entities = ids
            .Where(id => !existingRecipientIds.Contains(id))
            .Select(recipientId => new AppNotification
            {
                NotificationId = Guid.NewGuid(),
                RecipientUserId = recipientId,
                ActorUserId = request.ActorUserId,
                ClubId = request.ClubId,
                TeamId = request.TeamId,
                Type = Clean(request.Type, "General", 80),
                Priority = Clean(request.Priority, "Normal", 30),
                DeliveryPolicy = Clean(request.DeliveryPolicy, "RealtimeIfConnected", 40),
                Title = Clean(request.Title, "New notification", 200),
                Body = Clean(request.Body, string.Empty, 1000),
                TargetType = CleanNullable(request.TargetType, 80),
                TargetId = request.TargetId,
                TargetRoute = CleanNullable(request.TargetRoute, 300),
                MetadataJson = string.IsNullOrWhiteSpace(request.MetadataJson) ? null : request.MetadataJson,
                UniqueKey = string.IsNullOrWhiteSpace(request.UniqueKey) ? null : $"{request.UniqueKey}:{recipientId:N}",
                CreatedAt = now
            })
            .ToList();

        if (entities.Count == 0)
            return new List<NotificationDto>();

        _db.AppNotifications.AddRange(entities);
        await _db.SaveChangesAsync(cancellationToken);

        var dtos = await BuildDtosAsync(entities.Select(n => n.NotificationId).ToList(), cancellationToken);
        foreach (var dto in dtos)
        {
            await _publisher.PublishAsync(dto.RecipientUserId, dto, cancellationToken);
        }

        await SendCriticalEmailsIfNeededAsync(entities, cancellationToken);
        return dtos;
    }

    public async Task<NotificationListDto> GetMyNotificationsAsync(
        Guid userId,
        int page,
        int pageSize,
        bool unreadOnly,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.AppNotifications
            .Where(n => n.RecipientUserId == userId);

        if (unreadOnly)
            query = query.Where(n => n.ReadAt == null);

        var total = await query.CountAsync(cancellationToken);
        var unread = await GetUnreadCountAsync(userId, cancellationToken);
        var ids = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => n.NotificationId)
            .ToListAsync(cancellationToken);

        return new NotificationListDto
        {
            Items = await BuildDtosAsync(ids, cancellationToken),
            TotalCount = total,
            UnreadCount = unread
        };
    }

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _db.AppNotifications.CountAsync(n => n.RecipientUserId == userId && n.ReadAt == null, cancellationToken);

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        await _db.AppNotifications
            .Where(n => n.NotificationId == notificationId && n.RecipientUserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await _db.AppNotifications
            .Where(n => n.RecipientUserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task CleanupOldNotificationsAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default)
    {
        await _db.AppNotifications
            .Where(n => n.CreatedAt < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task SendMedicalReturnDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var records = await _db.MedicalRecords
            .Include(m => m.Team)
            .Include(m => m.Player)
                .ThenInclude(p => p.User)
            .Where(m => !m.IsCleared && m.ExpectedReturnDate != null && m.ExpectedReturnDate <= today)
            .ToListAsync(cancellationToken);

        foreach (var record in records)
        {
            var doctorIds = await _db.TeamMemberships
                .Where(tm => tm.TeamId == record.TeamId &&
                    tm.Status == MembershipStatus.Active &&
                    tm.Role == RoleNameType.TeamDoctor)
                .Select(tm => tm.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (record.DoctorUserId.HasValue && !doctorIds.Contains(record.DoctorUserId.Value))
                doctorIds.Add(record.DoctorUserId.Value);

            await CreateForUsersAsync(doctorIds, new CreateNotificationRequest
            {
                TeamId = record.TeamId,
                ClubId = record.Team.ClubId,
                Type = "MedicalReturnDue",
                Priority = "High",
                DeliveryPolicy = "RealtimeIfConnected",
                Title = "Player may be ready for clearance",
                Body = $"{record.Player.User.Name}'s expected return date has arrived. Review the medical record and update clearance.",
                TargetType = "MedicalRecord",
                TargetId = record.RecordId,
                TargetRoute = $"/teams/{record.TeamId}/medical/{record.RecordId}",
                UniqueKey = $"medical-return-due:{record.RecordId:N}"
            }, cancellationToken);
        }
    }

    private async Task<List<NotificationDto>> BuildDtosAsync(List<Guid> notificationIds, CancellationToken cancellationToken)
    {
        if (notificationIds.Count == 0)
            return new List<NotificationDto>();

        return await _db.AppNotifications
            .Include(n => n.ActorUser)
            .Include(n => n.Team)
            .Where(n => notificationIds.Contains(n.NotificationId))
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NotificationDto
            {
                NotificationId = n.NotificationId,
                RecipientUserId = n.RecipientUserId,
                ActorUserId = n.ActorUserId,
                ActorName = n.ActorUser == null ? null : n.ActorUser.Name,
                ClubId = n.ClubId,
                TeamId = n.TeamId,
                TeamName = n.Team == null ? null : n.Team.TeamName,
                Type = n.Type,
                Priority = n.Priority,
                DeliveryPolicy = n.DeliveryPolicy,
                Title = n.Title,
                Body = n.Body,
                TargetType = n.TargetType,
                TargetId = n.TargetId,
                TargetRoute = n.TargetRoute,
                MetadataJson = n.MetadataJson,
                CreatedAt = n.CreatedAt,
                ReadAt = n.ReadAt
            })
            .ToListAsync(cancellationToken);
    }

    private async Task SendCriticalEmailsIfNeededAsync(List<AppNotification> notifications, CancellationToken cancellationToken)
    {
        var emailCandidates = notifications
            .Where(n => n.DeliveryPolicy == EmailCriticalAndUnread || n.Priority == CriticalPriority)
            .Where(n => !_connectionTracker.IsConnected(n.RecipientUserId))
            .ToList();

        if (emailCandidates.Count == 0)
            return;

        var recipientIds = emailCandidates.Select(n => n.RecipientUserId).Distinct().ToList();
        var users = await _db.Users
            .Where(u => recipientIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.Email })
            .ToDictionaryAsync(u => u.UserId, cancellationToken);

        foreach (var notification in emailCandidates)
        {
            if (!users.TryGetValue(notification.RecipientUserId, out var user) || string.IsNullOrWhiteSpace(user.Email))
                continue;

            try
            {
                await _emailService.SendNotificationEmailAsync(user.Email, notification.Title, notification.Body);
                notification.EmailSentAt = DateTime.UtcNow;
            }
            catch
            {
                // Notification persistence is the source of truth; email failure must not break the user workflow.
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string Clean(string value, string fallback, int maxLength)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static string? CleanNullable(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var clean = value.Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }
}
