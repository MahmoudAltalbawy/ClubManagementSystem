using NpgsqlTypes;

namespace SportsPlatform.Auth.Core.Enums;

public enum RoleNameType
{
    [PgName("Admin")]
    Admin,
    [PgName("ClubManager")]
    ClubManager,
    [PgName("TeamManager")]
    TeamManager,
    [PgName("Coach")]
    Coach,
    [PgName("TeamAnalyst")]
    TeamAnalyst,
    [PgName("TeamDoctor")]
    TeamDoctor,
    [PgName("FitnessCoach")]
    FitnessCoach,
    [PgName("Player")]
    Player
}
