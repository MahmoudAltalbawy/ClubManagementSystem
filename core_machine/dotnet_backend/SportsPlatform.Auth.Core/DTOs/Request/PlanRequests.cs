namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreatePlanRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Visibility { get; set; } = "Draft";
}

public class UpdatePlanRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Visibility { get; set; } = "Draft";
}
