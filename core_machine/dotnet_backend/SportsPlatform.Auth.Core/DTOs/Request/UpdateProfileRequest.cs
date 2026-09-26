namespace SportsPlatform.Auth.Core.DTOs.Request;

public class UpdateProfileRequest
{
    public string? Name { get; set; }
    public string? Username { get; set; }
    public string? Bio { get; set; }
    public DateOnly? Dob { get; set; }
    public string? PhoneNumber { get; set; }
    public int? YearsOfExperience { get; set; }
}
