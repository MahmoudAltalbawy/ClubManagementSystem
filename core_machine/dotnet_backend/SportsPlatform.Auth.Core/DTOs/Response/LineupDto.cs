namespace SportsPlatform.Auth.Core.DTOs.Response;

public class LineupDto
{
    public Guid LineupId { get; set; }
    public Guid TeamId { get; set; }
    public Guid? EventId { get; set; }
    public Guid? SeasonId { get; set; }
    public string? EventTitle { get; set; }
    public DateTime? EventStartAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Formation { get; set; }
    public string? GameModel { get; set; }
    public string? TacticalNotes { get; set; }
    public string Visibility { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<LineupPlayerDto> Players { get; set; } = new();
}

public class LineupPlayerDto
{
    public Guid LineupPlayerId { get; set; }
    public Guid PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? Instructions { get; set; }
}
