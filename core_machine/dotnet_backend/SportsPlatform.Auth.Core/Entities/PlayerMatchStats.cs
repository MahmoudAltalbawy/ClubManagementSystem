namespace SportsPlatform.Auth.Core.Entities;

public class PlayerMatchStats
{
    public Guid PlayerMatchStatsId { get; set; }
    public Guid MatchStatsId { get; set; }
    public Guid TeamId { get; set; }
    public Guid EventId { get; set; }
    public Guid SeasonId { get; set; }
    public Guid PlayerUserId { get; set; }
    public int? MinutesPlayed { get; set; }
    public int? Goals { get; set; }
    public int? Assists { get; set; }
    public int? ShotsOnTarget { get; set; }
    public int? TotalShots { get; set; }
    public int? PassesCompleted { get; set; }
    public int? PassesAttempted { get; set; }
    public decimal? PassAccuracy { get; set; }
    public int? Tackles { get; set; }
    public int? Interceptions { get; set; }
    public int? YellowCards { get; set; }
    public int? RedCards { get; set; }
    public decimal? Rating { get; set; }
    public string? Notes { get; set; }

    // ── Basketball-specific player stats ──
    public string? Granularity { get; set; }         // "game_player" | "cumulative_player"
    public string? RowType { get; set; }             // "player" | "team_total"
    public string? Status { get; set; }              // "PLAYED" | "DNP" | "CUMULATIVE"
    public int? PlayerNo { get; set; }
    public bool? IsStarter { get; set; }
    public bool? IsCaptain { get; set; }
    public int? GamesListed { get; set; }
    public int? GamesPlayed { get; set; }
    public int? Starts { get; set; }
    public string? BbMinutes { get; set; }           // "mm:ss" format
    public string? TwoPtMA { get; set; }             // "made/attempted"
    public string? ThreePtMA { get; set; }
    public string? FtMA { get; set; }
    public int? OffensiveRebounds { get; set; }
    public int? DefensiveRebounds { get; set; }
    public int? TotalRebounds { get; set; }
    public int? BbAssists { get; set; }
    public int? BbTurnovers { get; set; }
    public int? BbSteals { get; set; }
    public int? BbBlocks { get; set; }
    public int? BbPersonalFouls { get; set; }
    public int? BbFoulsDrawn { get; set; }
    public int? BbEfficiency { get; set; }
    public int? BbPoints { get; set; }
    public int? BbTeamOffReb { get; set; }
    public int? BbTeamDefReb { get; set; }
    public int? BbTeamReb { get; set; }
    public int? BbTeamPF { get; set; }
    public int? BbTeamFD { get; set; }

    public MatchStats MatchStats { get; set; } = null!;
    public Team Team { get; set; } = null!;
    public Event Event { get; set; } = null!;
    public Season Season { get; set; } = null!;
    public User Player { get; set; } = null!;
}
