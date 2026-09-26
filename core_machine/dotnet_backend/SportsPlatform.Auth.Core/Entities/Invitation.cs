using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.Entities;

public class Invitation
{
    public Guid InvitationId { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public RoleNameType Role { get; set; }
    public Guid ClubId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid InvitedBy { get; set; }
    public InvitationStatus Status { get; set; }
    public string? PlayerPosition { get; set; }
    public int? JerseyNumber { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Club Club { get; set; } = null!;
    public Team? Team { get; set; }
    public User InvitedByUser { get; set; } = null!;
    public User? AcceptedByUser { get; set; }
}
