using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Controllers;

/// <summary>
/// Stores and serves the raw stats PDF for a match, plus a pre-built text
/// "context" payload. The "Ask Equipo" chatbot is not built yet; these
/// endpoints exist so the backend is ready when it is.
/// </summary>
[ApiController]
[Authorize]
public class MatchStatsPdfController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IChatbotWebhookDispatcher _chatbotWebhook;
    private readonly IConfiguration _config;
    private readonly IFileStorageService _storage;

    public MatchStatsPdfController(
        AppDbContext db,
        IWebHostEnvironment env,
        IChatbotWebhookDispatcher chatbotWebhook,
        IConfiguration config,
        IFileStorageService storage)
    {
        _db = db;
        _env = env;
        _chatbotWebhook = chatbotWebhook;
        _config = config;
        _storage = storage;
    }

    /// <summary>Attach (or replace) the raw stats PDF for a recorded match.</summary>
    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/stats/matches/{eventId:guid}/raw-pdf")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadRawPdf(
        Guid clubId, Guid teamId, Guid eventId,
        [FromForm] IFormFile? file,
        [FromQuery] string? pdfType = null)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "A PDF file is required." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".pdf") return BadRequest(new { error = "Only PDF files are supported." });

        // Default to box score so existing callers (which omit pdfType) keep working.
        var pdfTypeValue = string.IsNullOrWhiteSpace(pdfType)
            ? MatchStatsPdfType.BoxScore
            : pdfType.Trim().ToLowerInvariant();
        if (!MatchStatsPdfType.IsValid(pdfTypeValue))
            return BadRequest(new { error = $"Unknown pdfType '{pdfType}'. Allowed: box_score, plus_minus, lineup, play_by_play." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        var isAdmin = await IsAdminAsync(userId.Value);
        if (!isAdmin && !IsStatsStaff(role) && !await IsClubManagerAsync(clubId, userId.Value))
            return Forbid();

        var stats = await _db.MatchStats
            .Include(s => s.Documents)
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == eventId);
        if (stats == null) return NotFound(new { error = "Match stats not found. Record stats before attaching a PDF." });

        // Use a temporary file for pdftotext extraction
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/pdf" : file.ContentType;
            var extractedText = await TryExtractPdfTextAsync(tempPath);

            var category = $"match-stats/{eventId}/{pdfTypeValue}";
            string storagePath;
            await using (var readStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
            {
                storagePath = await _storage.SaveFileAsync(readStream, file.FileName, category, contentType);
            }

        // Upsert the document of this type (replace semantics, best-effort cleanup of the old file).
        var doc = stats.Documents.FirstOrDefault(d => string.Equals(d.PdfType, pdfTypeValue, StringComparison.OrdinalIgnoreCase));
        if (doc == null)
        {
            doc = new MatchStatsDocument
            {
                DocumentId = Guid.NewGuid(),
                MatchStatsId = stats.MatchStatsId,
                PdfType = pdfTypeValue,
            };
            stats.Documents.Add(doc);
            _db.MatchStatsDocuments.Add(doc);
        }
        else if (!string.IsNullOrEmpty(doc.StoragePath) && doc.StoragePath != storagePath)
        {
            await _storage.DeleteFileAsync(doc.StoragePath);
        }
        doc.StoragePath = storagePath;
        doc.FileName = file.FileName;
        doc.ContentType = contentType;
        doc.FileSize = file.Length;
        doc.ExtractedText = extractedText;
        doc.UploadedAt = DateTime.UtcNow;

        // Keep the legacy single-PDF fields mirroring the box score for backward compatibility.
        if (pdfTypeValue == MatchStatsPdfType.BoxScore)
        {
            if (!string.IsNullOrEmpty(stats.RawPdfPath) && stats.RawPdfPath != storagePath)
                await _storage.DeleteFileAsync(stats.RawPdfPath);

            stats.RawPdfPath = storagePath;
            stats.RawPdfFileName = file.FileName;
            stats.RawPdfContentType = contentType;
            stats.RawPdfSize = file.Length;
            stats.RawPdfUploadedAt = DateTime.UtcNow;
            stats.ExtractedText = extractedText;
        }
        stats.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Sport-gated, best-effort notify the "Ask Equipo" chatbot/prediction
        // microservice (basketball only). Pull-based: we send signed URLs, not bytes.
        if (IsBasketballMatch(stats, pdfTypeValue))
            await _chatbotWebhook.DispatchMatchStatsUpdatedAsync(BuildWebhookPayload(stats));

        return Ok(new
        {
            stats.EventId,
            PdfType = pdfTypeValue,
            FileName = doc.FileName,
            ContentType = doc.ContentType,
            FileSizeBytes = doc.FileSize,
            UploadedAt = doc.UploadedAt,
            HasExtractedText = !string.IsNullOrEmpty(doc.ExtractedText),
            StoredTypes = stats.Documents.Select(d => d.PdfType).OrderBy(t => t).ToArray(),
        });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>Download the stored raw stats PDF.</summary>
    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/matches/{eventId:guid}/raw-pdf")]
    public async Task<IActionResult> DownloadRawPdf(Guid clubId, Guid teamId, Guid eventId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value)) return Forbid();

        var stats = await _db.MatchStats
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == eventId);
        if (stats == null || string.IsNullOrEmpty(stats.RawPdfPath))
            return NotFound(new { error = "No PDF stored for this match." });
        if (stats.RawPdfPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(stats.RawPdfPath);
        }

        var fullPath = Path.Combine(_env.WebRootPath, stats.RawPdfPath.TrimStart('/'));
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "File not found on server." });

        return PhysicalFile(fullPath, stats.RawPdfContentType ?? "application/pdf", stats.RawPdfFileName ?? "stats.pdf");
    }

    /// <summary>Download a stored stats PDF of a specific type (box_score | plus_minus | lineup | play_by_play).</summary>
    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/matches/{eventId:guid}/raw-pdf/{pdfType}")]
    public async Task<IActionResult> DownloadDocument(Guid clubId, Guid teamId, Guid eventId, string pdfType)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value)) return Forbid();

        var pdfTypeValue = (pdfType ?? string.Empty).Trim().ToLowerInvariant();
        if (!MatchStatsPdfType.IsValid(pdfTypeValue))
            return BadRequest(new { error = $"Unknown pdfType '{pdfType}'. Allowed: box_score, plus_minus, lineup, play_by_play." });

        var stats = await _db.MatchStats
            .Include(s => s.Documents)
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == eventId);
        if (stats == null) return NotFound(new { error = "Match stats not found." });

        var doc = stats.Documents.FirstOrDefault(d => string.Equals(d.PdfType, pdfTypeValue, StringComparison.OrdinalIgnoreCase));
        if (doc == null || string.IsNullOrEmpty(doc.StoragePath))
            return NotFound(new { error = $"No {pdfTypeValue} PDF stored for this match." });
        if (doc.StoragePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(doc.StoragePath);
        }

        var fullPath = Path.Combine(_env.WebRootPath, doc.StoragePath.TrimStart('/'));
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "File not found on server." });

        return PhysicalFile(fullPath, doc.ContentType ?? "application/pdf", doc.FileName ?? $"{pdfTypeValue}.pdf");
    }

    /// <summary>
    /// Machine-readable context for the future "Ask Equipo" chatbot: a textual
    /// summary built from the structured stats, plus the extracted PDF text.
    /// </summary>
    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/matches/{eventId:guid}/context")]
    public async Task<IActionResult> GetContext(Guid clubId, Guid teamId, Guid eventId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value)) return Forbid();

        var stats = await _db.MatchStats
            .Include(s => s.Event)
            .Include(s => s.Documents)
            .Include(s => s.PlayerStats)
                .ThenInclude(ps => ps.Player)
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.EventId == eventId);
        if (stats == null) return NotFound(new { error = "Match stats not found." });

        return Ok(new
        {
            stats.EventId,
            stats.TeamId,
            EventTitle = stats.Event.Title,
            EventStartAt = stats.Event.StartAt,
            HasRawPdf = !string.IsNullOrEmpty(stats.RawPdfPath),
            RawPdfFileName = stats.RawPdfFileName,
            Documents = stats.Documents
                .OrderBy(d => d.PdfType)
                .Select(d => new
                {
                    d.PdfType,
                    d.FileName,
                    d.ContentType,
                    FileSizeBytes = d.FileSize,
                    d.UploadedAt,
                    HasExtractedText = !string.IsNullOrEmpty(d.ExtractedText),
                })
                .ToArray(),
            Summary = BuildSummary(stats),
            ExtractedText = stats.ExtractedText,
        });
    }

    /// <summary>
    /// The chatbot/prediction microservice is FIBA-basketball-only, so we only
    /// dispatch for basketball matches. plus_minus/lineup/play_by_play are
    /// basketball-only PDF concepts; box-score uploads are gated on basketball
    /// signals present in the recorded team stats so soccer matches don't fire.
    /// </summary>
    private static bool IsBasketballMatch(MatchStats s, string pdfType)
    {
        if (pdfType is MatchStatsPdfType.PlusMinus or MatchStatsPdfType.Lineup or MatchStatsPdfType.PlayByPlay)
            return true;

        return !string.IsNullOrWhiteSpace(s.Granularity)
            || !string.IsNullOrWhiteSpace(s.TwoPtMA)
            || !string.IsNullOrWhiteSpace(s.ThreePtMA)
            || !string.IsNullOrWhiteSpace(s.FtMA)
            || s.Points.HasValue
            || s.TotalRebounds.HasValue
            || s.BbAssists.HasValue;
    }

    private MatchStatsWebhookPayload BuildWebhookPayload(MatchStats stats)
    {
        // Prefer an explicit public base (e.g. behind a reverse proxy / container DNS);
        // fall back to the inbound request's scheme+host.
        var configured = _config["Microservice:PublicBaseUrl"];
        var baseUrl = !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : $"{Request.Scheme}://{Request.Host}";

        var documents = stats.Documents
            .OrderBy(d => d.PdfType)
            .Select(d => new WebhookDocumentRef
            {
                PdfType = d.PdfType,
                FileName = d.FileName,
                PullUrl = $"{baseUrl}/internal/match-stats/{stats.MatchStatsId}/documents/{d.PdfType}",
            })
            .ToList();

        // App-extracted box score is canonical; the microservice trusts it verbatim
        // and only self-extracts the other PDF types.
        var boxScore = stats.Documents
            .FirstOrDefault(d => d.PdfType == MatchStatsPdfType.BoxScore)?.ExtractedText
            ?? stats.ExtractedText;

        return new MatchStatsWebhookPayload
        {
            TeamId = stats.TeamId,
            EventId = stats.EventId,
            MatchStatsId = stats.MatchStatsId,
            BoxScoreText = boxScore,
            Documents = documents,
        };
    }

    private static string BuildSummary(MatchStats s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Match: {s.Event.Title} on {s.Event.StartAt:yyyy-MM-dd}.");
        if (!string.IsNullOrWhiteSpace(s.OpponentName)) sb.AppendLine($"Opponent: {s.OpponentName}.");
        if (s.TeamScore.HasValue || s.OpponentScore.HasValue)
            sb.AppendLine($"Score: {s.TeamScore?.ToString() ?? "-"} - {s.OpponentScore?.ToString() ?? "-"}.");
        if (!string.IsNullOrWhiteSpace(s.Result)) sb.AppendLine($"Result: {s.Result}.");
        if (!string.IsNullOrWhiteSpace(s.Venue)) sb.AppendLine($"Venue: {s.Venue}.");
        if (!string.IsNullOrWhiteSpace(s.CompetitionName)) sb.AppendLine($"Competition: {s.CompetitionName}.");

        // Basketball team totals.
        if (!string.IsNullOrWhiteSpace(s.Matchup)) sb.AppendLine($"Matchup: {s.Matchup}.");
        if (s.Points.HasValue) sb.AppendLine($"Points: {s.Points}.");
        if (s.TotalRebounds.HasValue) sb.AppendLine($"Rebounds: {s.TotalRebounds} (off {s.OffensiveRebounds?.ToString() ?? "-"}, def {s.DefensiveRebounds?.ToString() ?? "-"}).");
        if (s.BbAssists.HasValue) sb.AppendLine($"Assists: {s.BbAssists}.");
        if (s.Steals.HasValue) sb.AppendLine($"Steals: {s.Steals}.");
        if (s.Blocks.HasValue) sb.AppendLine($"Blocks: {s.Blocks}.");
        if (s.Turnovers.HasValue) sb.AppendLine($"Turnovers: {s.Turnovers}.");
        if (!string.IsNullOrWhiteSpace(s.TwoPtMA)) sb.AppendLine($"2PT made/attempted: {s.TwoPtMA}.");
        if (!string.IsNullOrWhiteSpace(s.ThreePtMA)) sb.AppendLine($"3PT made/attempted: {s.ThreePtMA}.");
        if (!string.IsNullOrWhiteSpace(s.FtMA)) sb.AppendLine($"FT made/attempted: {s.FtMA}.");

        // Soccer team totals.
        if (s.TotalGoals.HasValue) sb.AppendLine($"Goals: {s.TotalGoals}.");
        if (s.TotalShots.HasValue) sb.AppendLine($"Shots: {s.TotalShots} ({s.ShotsOnTarget?.ToString() ?? "-"} on target).");
        if (s.PossessionPercent.HasValue) sb.AppendLine($"Possession: {s.PossessionPercent}%.");
        if (s.PassAccuracy.HasValue) sb.AppendLine($"Pass accuracy: {s.PassAccuracy}%.");

        if (!string.IsNullOrWhiteSpace(s.Notes)) sb.AppendLine($"Notes: {s.Notes}.");

        if (s.PlayerStats.Count > 0)
        {
            sb.AppendLine("Players:");
            foreach (var p in s.PlayerStats.OrderBy(p => p.Player.Name))
            {
                var pts = p.BbPoints?.ToString() ?? p.Goals?.ToString();
                sb.AppendLine($"- {p.Player.Name}" + (pts != null ? $": {pts} pts/goals." : "."));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<string?> TryExtractPdfTextAsync(string pdfPath)
    {
        try
        {
            var toolPath = FindPdfToText();
            if (toolPath == null) return null;

            var tempTxt = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
            try
            {
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = toolPath,
                    ArgumentList = { "-layout", "-enc", "UTF-8", pdfPath, tempTxt },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process == null) return null;
                await process.WaitForExitAsync();
                if (process.ExitCode != 0 || !System.IO.File.Exists(tempTxt)) return null;
                return await System.IO.File.ReadAllTextAsync(tempTxt, Encoding.UTF8);
            }
            finally
            {
                TryDelete(tempTxt);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? FindPdfToText()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files\Git\mingw64\bin\pdftotext.exe",
            @"C:\Program Files\Git\usr\bin\pdftotext.exe",
            @"C:\Program Files\poppler\Library\bin\pdftotext.exe",
            @"C:\Program Files (x86)\poppler\Library\bin\pdftotext.exe",
            "/usr/bin/pdftotext",
            "/usr/local/bin/pdftotext",
        };

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(dir => new[]
            {
                Path.Combine(dir, "pdftotext.exe"),
                Path.Combine(dir, "pdftotext"),
            }));

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }

    private Task<bool> IsAdminAsync(Guid userId) =>
        _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);

    private async Task<RoleNameType?> GetTeamRoleAsync(Guid teamId, Guid userId)
    {
        return await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active)
            .Select(tm => (RoleNameType?)tm.Role)
            .FirstOrDefaultAsync();
    }

    private async Task<bool> CanViewTeamAsync(Guid? clubId, Guid teamId, Guid userId)
    {
        if (await IsAdminAsync(userId)) return true;
        if (await _db.TeamMemberships.AnyAsync(tm =>
                tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active))
            return true;
        return clubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId.Value && cm.UserId == userId && cm.Status == MembershipStatus.Active);
    }

    private Task<bool> IsClubManagerAsync(Guid clubId, Guid userId) =>
        _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId && cm.UserId == userId &&
            cm.Role == RoleNameType.ClubManager && cm.Status == MembershipStatus.Active);

    private static bool IsStatsStaff(RoleNameType? role) =>
        role is RoleNameType.Coach or RoleNameType.TeamManager or RoleNameType.TeamAnalyst;
}
