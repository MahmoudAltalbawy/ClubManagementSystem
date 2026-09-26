using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.Entities;

public class TeamMembership
{
    public Guid TeamMembershipId { get; set; }
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public RoleNameType Role { get; set; }
    public MembershipStatus Status { get; set; }
    public Guid? InvitedBy { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team Team { get; set; } = null!;
    public User User { get; set; } = null!;
    public User? InvitedByUser { get; set; }
}
