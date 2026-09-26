using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class ClubService : IClubService
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IFileStorageService _fileStorage;

    public ClubService(AppDbContext db, ITokenService tokenService, IFileStorageService fileStorage)
    {
        _db = db;
        _tokenService = tokenService;
        _fileStorage = fileStorage;
    }

    public async Task<ClubDto> CreateClubAsync(Guid userId, CreateClubRequest request, Stream? logoStream = null, string? logoFileName = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId)
            ?? throw new InvalidOperationException("User not found.");

        var alreadyOwnsClub = await _db.Clubs
            .IgnoreQueryFilters()
            .AnyAsync(c => c.CreatedBy == userId && c.DeletedAt == null);

        if (alreadyOwnsClub)
            throw new InvalidOperationException("A user can only create one club.");

        var club = new Club
        {
            ClubId = Guid.NewGuid(),
            Name = request.Name.Trim(),
            LogoUrl = logoStream != null && !string.IsNullOrWhiteSpace(logoFileName)
                ? await _fileStorage.SaveFileAsync(logoStream, logoFileName, "logos")
                : request.LogoUrl,
            Location = request.Location?.Trim(),
            LocationLatitude = request.LocationLatitude,
            LocationLongitude = request.LocationLongitude,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Clubs.Add(club);
        await _db.SaveChangesAsync();

        return new ClubDto
        {
            ClubId = club.ClubId,
            Name = club.Name,
            LogoUrl = club.LogoUrl,
            Location = club.Location,
            LocationLatitude = club.LocationLatitude,
            LocationLongitude = club.LocationLongitude,
            ManagerUserId = user.UserId,
            ManagerName = user.Name,
            MemberCount = 1,
            TeamCount = 0,
            CreatedAt = club.CreatedAt
        };
    }

    public async Task<ClubDto> UpdateClubLogoAsync(Guid clubId, Guid callerUserId, Stream logoStream, string logoFileName)
    {
        var club = await _db.Clubs
            .IgnoreQueryFilters()
            .Include(c => c.Creator)
            .Include(c => c.Memberships)
            .Include(c => c.Teams)
            .FirstOrDefaultAsync(c => c.ClubId == clubId && c.DeletedAt == null)
            ?? throw new InvalidOperationException("Club not found.");

        var isAdmin = await IsAdminAsync(callerUserId);
        if (!isAdmin && club.CreatedBy != callerUserId)
            throw new UnauthorizedAccessException("Only the club manager or an admin can update this club logo.");

        var oldLogo = club.LogoUrl;
        club.LogoUrl = await _fileStorage.SaveFileAsync(logoStream, logoFileName, "logos");
        club.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        if (!string.IsNullOrWhiteSpace(oldLogo))
            await _fileStorage.DeleteFileAsync(oldLogo);

        return new ClubDto
        {
            ClubId = club.ClubId,
            Name = club.Name,
            LogoUrl = club.LogoUrl,
            Location = club.Location,
            LocationLatitude = club.LocationLatitude,
            LocationLongitude = club.LocationLongitude,
            ManagerUserId = club.CreatedBy,
            ManagerName = club.Creator.Name,
            MemberCount = 1 + club.Memberships.Count(m => m.Status == MembershipStatus.Active),
            TeamCount = club.Teams.Count(t => t.DeletedAt == null),
            CreatedAt = club.CreatedAt
        };
    }

    public async Task<ClubDto> GetClubAsync(Guid clubId, Guid callerUserId)
    {
        var club = await _db.Clubs
            .IgnoreQueryFilters()
            .Include(c => c.Creator)
            .Include(c => c.Memberships)
            .Include(c => c.Teams)
            .FirstOrDefaultAsync(c => c.ClubId == clubId && c.DeletedAt == null)
            ?? throw new InvalidOperationException("Club not found.");

        await EnsureClubAccessAsync(clubId, callerUserId);

        return new ClubDto
        {
            ClubId = club.ClubId,
            Name = club.Name,
            LogoUrl = club.LogoUrl,
            Location = club.Location,
            LocationLatitude = club.LocationLatitude,
            LocationLongitude = club.LocationLongitude,
            ManagerUserId = club.CreatedBy,
            ManagerName = club.Creator.Name,
            MemberCount = 1 + club.Memberships.Count(m => m.Status == MembershipStatus.Active),
            TeamCount = club.Teams.Count(t => t.DeletedAt == null),
            CreatedAt = club.CreatedAt
        };
    }

    public async Task<List<ClubSummaryDto>> GetMyClubsAsync(Guid userId)
    {
        if (await IsAdminAsync(userId))
        {
            return await _db.Clubs
                .Where(c => c.DeletedAt == null)
                .OrderBy(c => c.Name)
                .Select(c => new ClubSummaryDto
                {
                    ClubId = c.ClubId,
                    Name = c.Name,
                    LogoUrl = c.LogoUrl,
                    Location = c.Location,
                    LocationLatitude = c.LocationLatitude,
                    LocationLongitude = c.LocationLongitude,
                    MyRole = RoleNameType.Admin.ToString()
                })
                .ToListAsync();
        }

        var ownedClubs = await _db.Clubs
            .Where(c => c.CreatedBy == userId)
            .Select(c => new ClubSummaryDto
            {
                ClubId = c.ClubId,
                Name = c.Name,
                LogoUrl = c.LogoUrl,
                Location = c.Location,
                LocationLatitude = c.LocationLatitude,
                LocationLongitude = c.LocationLongitude,
                MyRole = RoleNameType.ClubManager.ToString()
            })
            .ToListAsync();

        var memberships = await _db.ClubMemberships
            .Include(cm => cm.Club)
            .Where(cm => cm.UserId == userId && cm.Status == MembershipStatus.Active)
            .Select(cm => new ClubSummaryDto
            {
                ClubId = cm.ClubId,
                Name = cm.Club.Name,
                LogoUrl = cm.Club.LogoUrl,
                Location = cm.Club.Location,
                LocationLatitude = cm.Club.LocationLatitude,
                LocationLongitude = cm.Club.LocationLongitude,
                MyRole = cm.Role.ToString()
            })
            .ToListAsync();

        return ownedClubs
            .Concat(memberships)
            .GroupBy(c => c.ClubId)
            .Select(g => g.First())
            .OrderBy(c => c.Name)
            .ToList();
    }

    public async Task DeleteClubAsync(Guid clubId, Guid callerUserId)
    {
        var club = await _db.Clubs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ClubId == clubId && c.DeletedAt == null)
            ?? throw new InvalidOperationException("Club not found.");

        var isAdmin = await IsAdminAsync(callerUserId);
        if (!isAdmin && club.CreatedBy != callerUserId)
            throw new UnauthorizedAccessException("Only the club manager or an admin can delete this club.");

        var now = DateTime.UtcNow;
        club.DeletedAt = now;
        club.UpdatedAt = now;

        var teams = await _db.Teams
            .IgnoreQueryFilters()
            .Where(t => t.ClubId == clubId && t.DeletedAt == null)
            .ToListAsync();

        foreach (var team in teams)
        {
            team.DeletedAt = now;
            team.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<ClubMemberDto>> GetClubMembersAsync(Guid clubId, Guid callerUserId)
    {
        var club = await _db.Clubs
            .Include(c => c.Creator)
            .FirstOrDefaultAsync(c => c.ClubId == clubId)
            ?? throw new InvalidOperationException("Club not found.");

        await EnsureClubAccessAsync(clubId, callerUserId);

        var members = await _db.ClubMemberships
            .Include(cm => cm.User)
            .Where(cm => cm.ClubId == clubId && cm.Status == MembershipStatus.Active)
            .Select(cm => new ClubMemberDto
            {
                UserId = cm.UserId,
                Name = cm.User.Name,
                Email = cm.User.Email,
                Role = cm.Role.ToString(),
                JoinedAt = cm.JoinedAt
            })
            .ToListAsync();

        members.Insert(0, new ClubMemberDto
        {
            UserId = club.CreatedBy,
            Name = club.Creator.Name,
            Email = club.Creator.Email,
            Role = RoleNameType.ClubManager.ToString(),
            JoinedAt = club.CreatedAt
        });

        return members;
    }

    public async Task RemoveClubMemberAsync(Guid clubId, Guid targetUserId, Guid callerUserId)
    {
        var club = await _db.Clubs
            .FirstOrDefaultAsync(c => c.ClubId == clubId)
            ?? throw new InvalidOperationException("Club not found.");

        var isAdmin = await IsAdminAsync(callerUserId);
        if (!isAdmin && club.CreatedBy != callerUserId)
            throw new UnauthorizedAccessException("Only the club manager or an admin can remove club members.");

        if (club.CreatedBy == targetUserId)
            throw new InvalidOperationException("The club manager cannot be removed from the club.");

        var membership = await _db.ClubMemberships
            .FirstOrDefaultAsync(cm =>
                cm.ClubId == clubId &&
                cm.UserId == targetUserId &&
                cm.Status == MembershipStatus.Active)
            ?? throw new InvalidOperationException("Club membership not found.");

        var now = DateTime.UtcNow;
        membership.Status = MembershipStatus.Revoked;
        membership.UpdatedAt = now;

        var teamIdsInClub = await _db.Teams
            .Where(t => t.ClubId == clubId && t.DeletedAt == null)
            .Select(t => t.TeamId)
            .ToListAsync();

        var teamMemberships = await _db.TeamMemberships
            .Where(tm =>
                tm.UserId == targetUserId &&
                teamIdsInClub.Contains(tm.TeamId) &&
                tm.Status == MembershipStatus.Active)
            .ToListAsync();

        foreach (var teamMembership in teamMemberships)
        {
            teamMembership.Status = MembershipStatus.Revoked;
            teamMembership.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
        await _tokenService.RevokeAllUserTokensAsync(targetUserId);
    }

    private async Task EnsureClubAccessAsync(Guid clubId, Guid callerUserId)
    {
        var isAdmin = await IsAdminAsync(callerUserId);
        if (isAdmin)
            return;

        var hasAccess = await _db.Clubs.AnyAsync(c => c.ClubId == clubId && c.CreatedBy == callerUserId)
            || await _db.ClubMemberships.AnyAsync(cm =>
                cm.ClubId == clubId &&
                cm.UserId == callerUserId &&
                cm.Status == MembershipStatus.Active);

        if (!hasAccess)
            throw new UnauthorizedAccessException("You do not have access to this club.");
    }

    private Task<bool> IsAdminAsync(Guid userId)
    {
        return _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);
    }
}
