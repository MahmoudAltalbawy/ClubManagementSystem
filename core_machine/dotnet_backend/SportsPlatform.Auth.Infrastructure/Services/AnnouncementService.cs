using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class AnnouncementService : IAnnouncementService
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly INotificationService _notifications;

    public AnnouncementService(AppDbContext db, IFileStorageService fileStorage, INotificationService notifications)
    {
        _db = db;
        _fileStorage = fileStorage;
        _notifications = notifications;
    }

    public async Task<AnnouncementDto> CreateAnnouncementAsync(Guid clubId, Guid teamId, Guid callerUserId, CreateAnnouncementRequest request, Stream? imageStream = null, string? imageFileName = null)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanPostAnnouncementAsync(team, callerUserId);

        if (!Enum.TryParse<AnnouncementPriority>(request.Priority, true, out var priority))
            priority = AnnouncementPriority.Normal;

        var now = DateTime.UtcNow;
        var entity = new Announcement
        {
            AnnouncementId = Guid.NewGuid(),
            TeamId = team.TeamId,
            CreatedBy = callerUserId,
            Title = request.Title.Trim(),
            Content = request.Content.Trim(),
            ImageUrl = imageStream != null && !string.IsNullOrWhiteSpace(imageFileName)
                ? await _fileStorage.SaveFileAsync(imageStream, imageFileName, "announcements")
                : string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim(),
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Announcements.Add(entity);
        await _db.SaveChangesAsync();

        await _notifications.CreateForTeamAsync(team.TeamId, callerUserId, new CreateNotificationRequest
        {
            ClubId = team.ClubId,
            TeamId = team.TeamId,
            Type = priority == AnnouncementPriority.Urgent ? "UrgentAnnouncement" : "AnnouncementCreated",
            Priority = priority == AnnouncementPriority.Urgent ? "Critical" : priority.ToString(),
            DeliveryPolicy = priority == AnnouncementPriority.Urgent ? "EmailIfCriticalAndUnread" : "RealtimeIfConnected",
            Title = priority == AnnouncementPriority.Urgent ? "Urgent announcement" : "New announcement",
            Body = entity.Title,
            TargetType = "Announcement",
            TargetId = entity.AnnouncementId,
            TargetRoute = $"/teams/{team.TeamId}/announcements/{entity.AnnouncementId}"
        });

        return await BuildDtoAsync(entity.AnnouncementId);
    }

    public async Task<List<AnnouncementDto>> GetTeamAnnouncementsAsync(Guid clubId, Guid teamId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var announcements = await _db.Announcements
            .Include(a => a.Creator)
            .Where(a => a.TeamId == teamId && a.DeletedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        var result = new List<AnnouncementDto>();
        foreach (var a in announcements)
        {
            result.Add(new AnnouncementDto
            {
                AnnouncementId = a.AnnouncementId,
                TeamId = a.TeamId,
                Title = a.Title,
                Content = a.Content,
                ImageUrl = a.ImageUrl,
                Priority = a.Priority.ToString(),
                CreatorName = a.Creator.Name,
                CreatorRole = await GetCreatorRoleAsync(team, a.CreatedBy),
                CreatorImageUrl = a.Creator.ProfileImageUrl,
                CreatedBy = a.CreatedBy,
                CreatedAt = a.CreatedAt
            });
        }

        return result;
    }

    public async Task<AnnouncementDto> UpdateAnnouncementAsync(Guid clubId, Guid teamId, Guid announcementId, Guid callerUserId, UpdateAnnouncementRequest request, Stream? imageStream = null, string? imageFileName = null)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var entity = await _db.Announcements
            .FirstOrDefaultAsync(a => a.AnnouncementId == announcementId && a.TeamId == teamId && a.DeletedAt == null)
            ?? throw new InvalidOperationException("Announcement not found.");

        EnsureAnnouncementAuthor(entity, callerUserId);

        if (!Enum.TryParse<AnnouncementPriority>(request.Priority, true, out var priority))
            priority = AnnouncementPriority.Normal;

        entity.Title = request.Title.Trim();
        entity.Content = request.Content.Trim();
        entity.ImageUrl = imageStream != null && !string.IsNullOrWhiteSpace(imageFileName)
            ? await _fileStorage.SaveFileAsync(imageStream, imageFileName, "announcements")
            : string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim();
        entity.Priority = priority;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return await BuildDtoAsync(entity.AnnouncementId);
    }

    public async Task DeleteAnnouncementAsync(Guid clubId, Guid teamId, Guid announcementId, Guid callerUserId)
    {
        var team = await GetTeamForClubAsync(clubId, teamId);
        await EnsureCanViewTeamAsync(team, callerUserId);

        var entity = await _db.Announcements
            .FirstOrDefaultAsync(a => a.AnnouncementId == announcementId && a.TeamId == teamId && a.DeletedAt == null)
            ?? throw new InvalidOperationException("Announcement not found.");

        EnsureAnnouncementAuthor(entity, callerUserId);

        entity.DeletedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<AnnouncementDto> BuildDtoAsync(Guid announcementId)
    {
        var a = await _db.Announcements
            .Include(x => x.Creator)
            .Include(x => x.Team)
            .FirstOrDefaultAsync(x => x.AnnouncementId == announcementId)
            ?? throw new InvalidOperationException("Announcement not found.");

        return new AnnouncementDto
        {
            AnnouncementId = a.AnnouncementId,
            TeamId = a.TeamId,
            Title = a.Title,
            Content = a.Content,
            ImageUrl = a.ImageUrl,
            Priority = a.Priority.ToString(),
            CreatorName = a.Creator.Name,
            CreatorRole = await GetCreatorRoleAsync(a.Team, a.CreatedBy),
            CreatorImageUrl = a.Creator.ProfileImageUrl,
            CreatedBy = a.CreatedBy,
            CreatedAt = a.CreatedAt
        };
    }

    private async Task<Team> GetTeamForClubAsync(Guid clubId, Guid teamId)
    {
        return await _db.Teams
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.ClubId == clubId && t.DeletedAt == null)
            ?? throw new InvalidOperationException("Team not found.");
    }

    private async Task EnsureCanPostAnnouncementAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;
        if (team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId)) return;
        var canPost = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId && tm.UserId == callerUserId &&
            tm.Role != RoleNameType.Player && tm.Status == MembershipStatus.Active);
        if (!canPost)
            throw new UnauthorizedAccessException("Only non-player team members can post announcements.");
    }

    private static void EnsureAnnouncementAuthor(Announcement announcement, Guid callerUserId)
    {
        if (announcement.CreatedBy != callerUserId)
            throw new UnauthorizedAccessException("Only the announcement author can edit or delete it.");
    }

    private async Task EnsureCanViewTeamAsync(Team team, Guid callerUserId)
    {
        if (await IsAdminAsync(callerUserId)) return;
        if (team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == callerUserId)) return;
        var hasClubMembership = team.ClubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == team.ClubId && cm.UserId == callerUserId && cm.Status == MembershipStatus.Active);
        var hasTeamMembership = await _db.TeamMemberships.AnyAsync(tm =>
            tm.TeamId == team.TeamId && tm.UserId == callerUserId && tm.Status == MembershipStatus.Active);
        if (!hasClubMembership && !hasTeamMembership)
            throw new UnauthorizedAccessException("You do not have access to this team.");
    }

    private Task<bool> IsAdminAsync(Guid userId) =>
        _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);

    private async Task<string> GetCreatorRoleAsync(Team team, Guid userId)
    {
        if (await IsAdminAsync(userId)) return RoleNameType.Admin.ToString();
        if (team.ClubId.HasValue && await _db.Clubs.AnyAsync(c => c.ClubId == team.ClubId && c.CreatedBy == userId))
            return RoleNameType.ClubManager.ToString();

        var role = await _db.TeamMemberships
            .Where(tm => tm.TeamId == team.TeamId && tm.UserId == userId && tm.Status == MembershipStatus.Active)
            .Select(tm => (RoleNameType?)tm.Role)
            .FirstOrDefaultAsync();

        return role?.ToString() ?? string.Empty;
    }
}
