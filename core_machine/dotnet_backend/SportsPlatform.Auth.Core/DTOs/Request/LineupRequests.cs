namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateLineupRequest
{
    public Guid? EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Formation { get; set; }
    public string? GameModel { get; set; }
    public string? TacticalNotes { get; set; }
    public string Visibility { get; set; } = "Draft";
    public List<LineupPlayerRequest> Players { get; set; } = new();
}

public class UpdateLineupRequest : CreateLineupRequest
{
}

public class LineupPlayerRequest
{
    public Guid PlayerUserId { get; set; }
    public string Position { get; set; } = string.Empty;
    public string Unit { get; set; } = "Starting";
    public int SortOrder { get; set; }
    public string? Instructions { get; set; }
}
