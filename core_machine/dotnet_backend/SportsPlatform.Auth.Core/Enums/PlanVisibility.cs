using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum PlanVisibility
{
    [PgName("Draft")] Draft,
    [PgName("TeamVisible")] TeamVisible,
    [PgName("PlayerAssigned")] PlayerAssigned
}
