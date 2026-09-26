namespace SportsPlatform.Auth.Core.DTOs.Response;

public class TeamCategoryDto
{
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? MinAge { get; set; }
    public int? MaxAge { get; set; }
}
