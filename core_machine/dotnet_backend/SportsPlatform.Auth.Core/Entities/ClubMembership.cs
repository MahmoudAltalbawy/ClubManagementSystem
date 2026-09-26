using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.Entities;

public class ClubMembership
{
    public Guid ClubMembershipId { get; set; }
    public Guid ClubId { get; set; }
    public Guid UserId { get; set; }
    public RoleNameType Role { get; set; }
    public MembershipStatus Status { get; set; }
    public Guid? InvitedBy { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Club Club { get; set; } = null!;
    public User User { get; set; } = null!;
    public User? InvitedByUser { get; set; }
}
