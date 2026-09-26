using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.Entities;

public class UserAuthProvider
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AuthProviderType Provider { get; set; }
    public string? ProviderUserId { get; set; }
    public string? ProviderIdentifier { get; set; }
    public string? PasswordHash { get; set; }
    public bool IsVerified { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}
