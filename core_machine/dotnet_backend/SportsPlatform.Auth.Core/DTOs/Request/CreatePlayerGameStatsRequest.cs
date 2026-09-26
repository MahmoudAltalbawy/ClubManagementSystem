namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreatePlayerGameStatsRequest
{
    public Guid PlayerUserId { get; set; }
    public Guid? EventId { get; set; }
    public DateOnly MatchDate { get; set; }
    public string? OpponentName { get; set; }
    public int? MinutesPlayed { get; set; }
    public int? Goals { get; set; }
    public int? Assists { get; set; }
    public int? YellowCards { get; set; }
    public int? RedCards { get; set; }
    public decimal? Rating { get; set; }
    public string? Notes { get; set; }
}
