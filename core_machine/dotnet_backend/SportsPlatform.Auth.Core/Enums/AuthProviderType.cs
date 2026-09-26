using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum AuthProviderType
{
    [PgName("local")]
    Local,
    [PgName("google")]
    Google,
    [PgName("apple")]
    Apple,
    [PgName("microsoft")]
    Microsoft,
    [PgName("phone")]
    Phone,
    [PgName("magic_link")]
    MagicLink
}
