using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum EventType
{
    [PgName("Match")] Match,
    [PgName("Training")] Training,
    [PgName("Meeting")] Meeting,
    [PgName("Test")] Test
}
