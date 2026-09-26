namespace SportsPlatform.Auth.Core.Entities;

public class MatchStats
{
    public Guid MatchStatsId { get; set; }
    public Guid TeamId { get; set; }
    public Guid EventId { get; set; }
    public Guid SeasonId { get; set; }
    public Guid RecordedBy { get; set; }
    public string? OpponentName { get; set; }
    public int? TeamScore { get; set; }
    public int? OpponentScore { get; set; }
    public string? Result { get; set; }
    public string? Venue { get; set; }
    public string? CompetitionName { get; set; }
    public decimal? PossessionPercent { get; set; }
    public int? TotalGoals { get; set; }
    public int? TotalAssists { get; set; }
    public int? ShotsOnTarget { get; set; }
    public int? TotalShots { get; set; }
    public int? PassesCompleted { get; set; }
    public int? PassesAttempted { get; set; }
    public decimal? PassAccuracy { get; set; }
    public int? Tackles { get; set; }
    public int? Interceptions { get; set; }
    public int? YellowCards { get; set; }
    public int? RedCards { get; set; }
    public string? Notes { get; set; }

    // ── Basketball-specific team stats ──
    public string? Category { get; set; }           // "game" | "training"
    public string? Granularity { get; set; }         // "game_team_total" | "cumulative_team_total"
    public string? GameNo { get; set; }
    public string? Matchup { get; set; }
    public string? TwoPtMA { get; set; }             // "made/attempted" e.g. "20/44"
    public string? ThreePtMA { get; set; }
    public string? FtMA { get; set; }
    public int? OffensiveRebounds { get; set; }
    public int? DefensiveRebounds { get; set; }
    public int? TotalRebounds { get; set; }
    public int? BbAssists { get; set; }              // basketball assists (Assists used by soccer)
    public int? Turnovers { get; set; }
    public int? Steals { get; set; }
    public int? Blocks { get; set; }
    public int? PersonalFouls { get; set; }
    public int? FoulsDrawn { get; set; }
    public int? Efficiency { get; set; }
    public int? Points { get; set; }
    public string? Minutes { get; set; }
    public int? TeamOffReb { get; set; }
    public int? TeamDefReb { get; set; }
    public int? TeamReb { get; set; }
    public int? TeamPF { get; set; }
    public int? TeamFD { get; set; }
    public string? SourceFile { get; set; }

    // ── Raw stats PDF (persisted for the future "Ask Equipo" chatbot) ──
    public string? RawPdfPath { get; set; }          // relative path under wwwroot
    public string? RawPdfFileName { get; set; }       // original upload filename
    public string? RawPdfContentType { get; set; }
    public long? RawPdfSize { get; set; }
    public DateTime? RawPdfUploadedAt { get; set; }
    public string? ExtractedText { get; set; }        // best-effort plain text of the PDF

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team Team { get; set; } = null!;
    public Event Event { get; set; } = null!;
    public Season Season { get; set; } = null!;
    public User Recorder { get; set; } = null!;
    public ICollection<PlayerMatchStats> PlayerStats { get; set; } = new List<PlayerMatchStats>();

    // Typed raw stats PDFs (box score, plus/minus, lineup, play-by-play) for the
    // "Ask Equipo" chatbot / prediction microservice. The legacy RawPdf* fields
    // above mirror the box-score document for backward compatibility.
    public ICollection<MatchStatsDocument> Documents { get; set; } = new List<MatchStatsDocument>();
}
