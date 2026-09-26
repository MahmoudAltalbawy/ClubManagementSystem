using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum MembershipStatus
{
    [PgName("Active")]
    Active,
    [PgName("Revoked")]
    Revoked,
    [PgName("Left")]
    Left
}
