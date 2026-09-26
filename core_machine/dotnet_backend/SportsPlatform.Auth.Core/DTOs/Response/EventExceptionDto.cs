namespace SportsPlatform.Auth.Core.DTOs.Response;

public class EventExceptionDto
{
    public Guid EventExceptionId { get; set; }
    public DateOnly OriginalDate { get; set; }
    public DateTime? NewStartAt { get; set; }
    public DateTime? NewEndAt { get; set; }
    public bool IsCancelled { get; set; }
    public string? Notes { get; set; }
}
