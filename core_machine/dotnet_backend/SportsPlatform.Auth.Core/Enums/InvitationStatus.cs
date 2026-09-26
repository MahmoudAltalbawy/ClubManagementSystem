using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum InvitationStatus
{
    [PgName("Pending")]
    Pending,
    [PgName("Accepted")]
    Accepted,
    [PgName("Expired")]
    Expired,
    [PgName("Cancelled")]
    Cancelled,
    [PgName("Denied")]
    Denied
}
