using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum AttendanceStatus
{
    [PgName("Present")] Present,
    [PgName("Absent")] Absent,
    [PgName("Late")] Late,
    [PgName("Excused")] Excused,
    [PgName("Injured")] Injured
}
