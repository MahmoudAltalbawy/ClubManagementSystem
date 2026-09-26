namespace SportsPlatform.Auth.Core.Entities;

/// <summary>
/// One raw stats PDF of a specific <see cref="MatchStatsPdfType"/> attached to a
/// <see cref="MatchStats"/>. A match can hold at most one document per type
/// (box score, plus/minus, lineup, play-by-play). These are the files the
/// "Ask Equipo" chatbot / prediction microservice pulls per game.
/// </summary>
public class MatchStatsDocument
{
    public Guid DocumentId { get; set; }
    public Guid MatchStatsId { get; set; }

    /// <summary>One of <see cref="MatchStatsPdfType"/>.</summary>
    public string PdfType { get; set; } = MatchStatsPdfType.BoxScore;

    public string StoragePath { get; set; } = string.Empty;   // absolute path under wwwroot/uploads
    public string FileName { get; set; } = string.Empty;       // original upload filename
    public string ContentType { get; set; } = "application/pdf";
    public long FileSize { get; set; }
    public string? ExtractedText { get; set; }                 // best-effort plain text of the PDF
    public DateTime UploadedAt { get; set; }

    public MatchStats MatchStats { get; set; } = null!;
}

/// <summary>
/// The four basketball PDF report types the prediction model consumes. Stored as
/// a string column (matching the existing string-typed convention used by
/// <c>match_analysis_document.document_type</c> / <c>match_stats.category</c>).
/// </summary>
public static class MatchStatsPdfType
{
    public const string BoxScore = "box_score";
    public const string PlusMinus = "plus_minus";
    public const string Lineup = "lineup";
    public const string PlayByPlay = "play_by_play";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BoxScore,
            PlusMinus,
            Lineup,
            PlayByPlay,
        };

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}
