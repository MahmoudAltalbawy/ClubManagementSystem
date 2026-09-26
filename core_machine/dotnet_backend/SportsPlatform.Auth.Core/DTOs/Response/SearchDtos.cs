namespace SportsPlatform.Auth.Core.DTOs.Response;

public class SearchResultDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? TargetId { get; set; }
    public string? TargetRoute { get; set; }
    public string? ImageUrl { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime? OccurredAt { get; set; }
}

public class SearchResponseDto
{
    public string Query { get; set; } = string.Empty;
    public string Type { get; set; } = "all";
    public int TotalCount { get; set; }
    public List<SearchResultDto> Results { get; set; } = new();
}
