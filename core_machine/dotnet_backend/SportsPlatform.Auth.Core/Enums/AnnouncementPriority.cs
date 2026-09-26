using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum AnnouncementPriority
{
    [PgName("Normal")] Normal,
    [PgName("Important")] Important,
    [PgName("Urgent")] Urgent
}
