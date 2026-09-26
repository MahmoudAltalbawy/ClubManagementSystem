using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Controllers;

/// <summary>
/// Service-to-service endpoints the chatbot/prediction microservice uses to PULL
/// match-stats PDFs the app told it about via webhook. These are NOT for end users:
/// they are guarded by a shared bearer service token, not the user JWT, and are
/// excluded from the normal [Authorize] pipeline via [AllowAnonymous] + manual check.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/match-stats")]
public class InternalMatchStatsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public InternalMatchStatsController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    /// <summary>Pull one stored stats PDF by match-stats id and type.</summary>
    [HttpGet("{matchStatsId:guid}/documents/{pdfType}")]
    public async Task<IActionResult> PullDocument(Guid matchStatsId, string pdfType)
    {
        if (!IsServiceTokenValid()) return Unauthorized(new { error = "Invalid service token." });

        var pdfTypeValue = (pdfType ?? string.Empty).Trim().ToLowerInvariant();

        var doc = await _db.MatchStatsDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.MatchStatsId == matchStatsId && d.PdfType == pdfTypeValue);
        if (doc == null || string.IsNullOrEmpty(doc.StoragePath))
            return NotFound(new { error = $"No {pdfTypeValue} PDF stored for this match." });
        if (doc.StoragePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            doc.StoragePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(doc.StoragePath);
        }

        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", doc.StoragePath.TrimStart('/'));
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "File not found on server." });

        return PhysicalFile(fullPath, doc.ContentType ?? "application/pdf", doc.FileName ?? $"{pdfTypeValue}.pdf");
    }

    /// <summary>
    /// Constant-time-ish comparison of the bearer token against Microservice:ServiceToken.
    /// Accepts "Authorization: Bearer &lt;token&gt;" or the "X-Service-Token" header.
    /// </summary>
    private bool IsServiceTokenValid()
    {
        var expected = _config["Microservice:ServiceToken"];
        if (string.IsNullOrWhiteSpace(expected)) return false; // fail closed if unset

        string? provided = null;
        var auth = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            provided = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(provided))
            provided = Request.Headers["X-Service-Token"].ToString();

        if (string.IsNullOrEmpty(provided)) return false;

        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
