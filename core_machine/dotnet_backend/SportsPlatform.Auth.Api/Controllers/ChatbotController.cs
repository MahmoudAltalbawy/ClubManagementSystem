using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;
using SportsPlatform.Auth.Infrastructure.Services;

namespace SportsPlatform.Auth.Api.Controllers;

/// <summary>
/// User-facing proxy for the "Ask Equipo" chatbot. The Flutter app calls this with
/// its normal user JWT; we authorize the caller against the team, then forward the
/// question to the chatbot/prediction microservice using the shared service-token
/// HttpClient. This keeps the microservice port internal — the app never talks to it
/// directly. project_id on the microservice == team_id here.
/// </summary>
[ApiController]
[Authorize]
[Route("chatbot")]
public class ChatbotController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChatbotWebhookDispatcher _chatbotWebhook;
    private readonly ILogger<ChatbotController> _logger;

    // Phase 9.6 idempotency marker: teams we've already backfilled (or confirmed
    // already-ingested) this process, so we don't re-scan on every ask. Durable
    // idempotency comes from the microservice status check on a cold start; this
    // dictionary just spares the round-trip while the process is warm.
    private static readonly ConcurrentDictionary<Guid, byte> BackfilledTeams = new();

    public ChatbotController(
        AppDbContext db,
        IConfiguration config,
        IHttpClientFactory httpClientFactory,
        IChatbotWebhookDispatcher chatbotWebhook,
        ILogger<ChatbotController> logger)
    {
        _db = db;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _chatbotWebhook = chatbotWebhook;
        _logger = logger;
    }

    /// <summary>Ask the chatbot a question scoped to a team the caller can view.</summary>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] ChatbotAskRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        if (request == null || string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "A question is required." });
        if (request.TeamId == Guid.Empty)
            return BadRequest(new { error = "A teamId is required." });

        // Scope: caller must be able to view this team (member, club member, or admin).
        if (!await CanViewTeamAsync(request.ClubId, request.TeamId, userId.Value))
            return Forbid();

        // Feature flag + config, same gate the webhook dispatcher uses.
        var enabled = bool.TryParse(_config["Microservice:Enabled"], out var e) && e;
        if (!enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "The chatbot is not enabled." });
        if (string.IsNullOrWhiteSpace(_config["Microservice:BaseUrl"]))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "The chatbot is not configured." });

        // Phase 9.6: on the first ask for a team the microservice has never ingested,
        // backfill its existing stored PDFs into the ingest+retrain pipeline so the
        // model/CSV reflect full history. Best-effort and idempotent — never blocks the
        // question (the dispatched work runs async on the microservice's per-team queue).
        await TryBackfillTeamAsync(request.TeamId);

        var forwardBody = new MicroserviceAskRequest
        {
            Question = request.Question.Trim(),
            SessionId = string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId,
        };
        if (!string.IsNullOrWhiteSpace(request.Team))
            forwardBody.Team = request.Team.Trim();
        // "session" => answer strictly from PDFs uploaded to this chat; default "team".
        if (!string.IsNullOrWhiteSpace(request.PdfScope))
            forwardBody.PdfScope = request.PdfScope.Trim().ToLowerInvariant();

        try
        {
            var client = _httpClientFactory.CreateClient(ChatbotWebhookDispatcher.HttpClientName);
            // project_id == team_id. Relative path against the configured BaseUrl.
            var response = await client.PostAsJsonAsync(
                $"projects/{request.TeamId}/ask", forwardBody, HttpContext.RequestAborted);

            var payload = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Chatbot ask for team {TeamId} returned {Status}.",
                    request.TeamId, (int)response.StatusCode);
            }

            // Relay the microservice's JSON (success or error) verbatim with its status code.
            return new ContentResult
            {
                Content = payload,
                ContentType = "application/json; charset=utf-8",
                StatusCode = (int)response.StatusCode,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chatbot ask for team {TeamId} failed.", request.TeamId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The chatbot is unavailable right now. Please try again." });
        }
    }

    /// <summary>List the caller's past chat sessions for a team they can view.</summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions(
        [FromQuery] Guid teamId, [FromQuery] Guid? clubId, [FromQuery] int? limit)
    {
        var (error, client) = await PrepareForwardAsync(teamId, clubId);
        if (error != null) return error;

        try
        {
            var url = $"projects/{teamId}/sessions" + (limit.HasValue ? $"?limit={limit.Value}" : string.Empty);
            var response = await client!.GetAsync(url, HttpContext.RequestAborted);
            return await RelayAsync(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chatbot list-sessions for team {TeamId} failed.", teamId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The chatbot is unavailable right now. Please try again." });
        }
    }

    /// <summary>Fetch the full transcript of one chat session.</summary>
    [HttpGet("sessions/{sessionId}")]
    public async Task<IActionResult> GetTranscript(
        string sessionId, [FromQuery] Guid teamId, [FromQuery] Guid? clubId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "A sessionId is required." });

        var (error, client) = await PrepareForwardAsync(teamId, clubId);
        if (error != null) return error;

        try
        {
            var response = await client!.GetAsync(
                $"projects/{teamId}/sessions/{Uri.EscapeDataString(sessionId)}", HttpContext.RequestAborted);
            return await RelayAsync(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chatbot transcript for team {TeamId} failed.", teamId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The chatbot is unavailable right now. Please try again." });
        }
    }

    /// <summary>Clear (delete) one chat session.</summary>
    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> ClearSession(
        string sessionId, [FromQuery] Guid teamId, [FromQuery] Guid? clubId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "A sessionId is required." });

        var (error, client) = await PrepareForwardAsync(teamId, clubId);
        if (error != null) return error;

        try
        {
            var response = await client!.DeleteAsync(
                $"projects/{teamId}/sessions/{Uri.EscapeDataString(sessionId)}", HttpContext.RequestAborted);
            return await RelayAsync(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chatbot clear-session for team {TeamId} failed.", teamId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The chatbot is unavailable right now. Please try again." });
        }
    }

    /// <summary>
    /// Upload one or more PDFs into a chat session. scope="team" (default) merges them
    /// into the team library; scope="session" isolates them so the coach can ask about
    /// that file only (pair with pdfScope="session" on the next ask).
    /// </summary>
    [HttpPost("uploads")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> UploadPdfs(
        [FromForm] Guid teamId,
        [FromForm] Guid? clubId,
        [FromForm] string? sessionId,
        [FromForm] string? scope,
        [FromForm] List<IFormFile> files)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { error = "At least one PDF file is required." });
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "A sessionId is required for in-chat uploads." });
        foreach (var file in files)
        {
            if (file.Length == 0)
                return BadRequest(new { error = $"{file.FileName} is empty." });
            if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Only PDF files are supported." });
        }

        var (error, client) = await PrepareForwardAsync(teamId, clubId);
        if (error != null) return error;

        var scopeValue = string.IsNullOrWhiteSpace(scope) ? "team" : scope.Trim().ToLowerInvariant();
        if (scopeValue != "team" && scopeValue != "session")
            return BadRequest(new { error = "scope must be 'team' or 'session'." });

        try
        {
            using var content = new MultipartFormDataContent();
            foreach (var file in files)
            {
                var part = new StreamContent(file.OpenReadStream());
                part.Headers.ContentType = new MediaTypeHeaderValue(
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/pdf" : file.ContentType);
                // Field name must be "files" to match the microservice's File(...) param.
                content.Add(part, "files", file.FileName);
            }

            var url = $"projects/{teamId}/sessions/{Uri.EscapeDataString(sessionId)}/uploads?scope={scopeValue}";
            var response = await client!.PostAsync(url, content, HttpContext.RequestAborted);
            return await RelayAsync(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chatbot upload for team {TeamId} failed.", teamId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The chatbot is unavailable right now. Please try again." });
        }
    }

    /// <summary>
    /// Shared gate for the proxy endpoints: authenticate, require a team, scope-check,
    /// and confirm the microservice is enabled/configured. Returns an error result to
    /// short-circuit on, or the configured HttpClient to forward with.
    /// </summary>
    private async Task<(IActionResult? error, HttpClient? client)> PrepareForwardAsync(Guid teamId, Guid? clubId)
    {
        var userId = GetCallerUserId();
        if (userId == null)
            return (Unauthorized(new { error = "Invalid token." }), null);
        if (teamId == Guid.Empty)
            return (BadRequest(new { error = "A teamId is required." }), null);
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value))
            return (Forbid(), null);

        var enabled = bool.TryParse(_config["Microservice:Enabled"], out var e) && e;
        if (!enabled)
            return (StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "The chatbot is not enabled." }), null);
        if (string.IsNullOrWhiteSpace(_config["Microservice:BaseUrl"]))
            return (StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "The chatbot is not configured." }), null);

        return (null, _httpClientFactory.CreateClient(ChatbotWebhookDispatcher.HttpClientName));
    }

    /// <summary>Relay a microservice response (success or error) verbatim with its status code.</summary>
    private async Task<IActionResult> RelayAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
        return new ContentResult
        {
            Content = payload,
            ContentType = "application/json; charset=utf-8",
            StatusCode = (int)response.StatusCode,
        };
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }

    private async Task<bool> CanViewTeamAsync(Guid? clubId, Guid teamId, Guid userId)
    {
        if (await _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin)) return true;
        if (await _db.TeamMemberships.AnyAsync(tm =>
                tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active))
            return true;
        return clubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId.Value && cm.UserId == userId && cm.Status == MembershipStatus.Active);
    }

    /// <summary>
    /// Phase 9.6 auto-backfill. If the microservice has never ingested this team
    /// (no PDFs/CSVs yet), re-feed its existing stored match PDFs by dispatching one
    /// "match stats updated" webhook per stored basketball match — exactly the path a
    /// fresh upload takes — so the model trains on full history without the coach
    /// re-uploading. Runs at most once per team per process; never throws.
    /// </summary>
    private async Task TryBackfillTeamAsync(Guid teamId)
    {
        if (BackfilledTeams.ContainsKey(teamId)) return;

        try
        {
            var client = _httpClientFactory.CreateClient(ChatbotWebhookDispatcher.HttpClientName);

            // Ask the microservice whether this team was ever ingested.
            var statusResp = await client.GetAsync(
                $"projects/{teamId}/status", HttpContext.RequestAborted);
            if (!statusResp.IsSuccessStatusCode)
            {
                // Can't determine state — don't mark; retry on a later ask.
                _logger.LogDebug(
                    "Backfill status check for team {TeamId} returned {Status}; will retry later.",
                    teamId, (int)statusResp.StatusCode);
                return;
            }

            var status = await statusResp.Content.ReadFromJsonAsync<MicroserviceProjectStatus>(
                cancellationToken: HttpContext.RequestAborted);

            // If the team does not have a prediction model generated yet, we MUST backfill
            // so it runs the prediction training.
            if (status != null && status.HasPredictionsCsv)
            {
                BackfilledTeams.TryAdd(teamId, 1);
                return;
            }

            // Never ingested: dispatch a webhook per stored basketball match. The
            // microservice pulls each match's PDFs and retrains on its serialized
            // per-team queue, so ordering is safe and the first prediction is correct.
            var matches = await _db.MatchStats
                .Include(s => s.Documents)
                .Where(s => s.TeamId == teamId && s.Documents.Any())
                .ToListAsync(HttpContext.RequestAborted);

            var dispatched = 0;
            foreach (var stats in matches)
            {
                if (!HasBasketballDocuments(stats)) continue;
                await _chatbotWebhook.DispatchMatchStatsUpdatedAsync(
                    BuildBackfillPayload(stats), HttpContext.RequestAborted);
                dispatched++;
            }

            _logger.LogInformation(
                "Backfill for team {TeamId}: dispatched {Count} stored match(es) to the chatbot microservice.",
                teamId, dispatched);

            // Mark done so we don't re-scan every ask this process. If dispatched == 0
            // there was nothing to ingest, which is also a terminal state for backfill.
            BackfilledTeams.TryAdd(teamId, 1);
        }
        catch (Exception ex)
        {
            // Never let backfill break the ask path; leave unmarked so a later ask retries.
            _logger.LogWarning(ex, "Backfill for team {TeamId} failed; will retry on a later ask.", teamId);
        }
    }

    /// <summary>
    /// Mirrors MatchStatsPdfController.IsBasketballMatch at the match level: the
    /// chatbot/prediction microservice is FIBA-basketball-only, so only basketball
    /// matches are backfilled. plus_minus/lineup/play_by_play are basketball-only PDF
    /// concepts; otherwise we gate on basketball signals in the recorded team stats.
    /// </summary>
    private static bool HasBasketballDocuments(MatchStats s)
    {
        var hasBasketballDoc = s.Documents.Any(d =>
            d.PdfType is MatchStatsPdfType.PlusMinus
                or MatchStatsPdfType.Lineup
                or MatchStatsPdfType.PlayByPlay);
        if (hasBasketballDoc) return true;

        return !string.IsNullOrWhiteSpace(s.Granularity)
            || !string.IsNullOrWhiteSpace(s.TwoPtMA)
            || !string.IsNullOrWhiteSpace(s.ThreePtMA)
            || !string.IsNullOrWhiteSpace(s.FtMA)
            || s.Points.HasValue
            || s.TotalRebounds.HasValue
            || s.BbAssists.HasValue;
    }

    /// <summary>
    /// Builds the same pull-based webhook payload the upload path sends, so backfill
    /// and live uploads are indistinguishable to the microservice. Signed pull-URLs,
    /// not bytes; app-extracted box score is canonical.
    /// </summary>
    private MatchStatsWebhookPayload BuildBackfillPayload(MatchStats stats)
    {
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
}

/// <summary>Body the Flutter app sends. team_id is the active team (== microservice project_id).</summary>
public sealed class ChatbotAskRequest
{
    [JsonPropertyName("teamId")] public Guid TeamId { get; set; }
    [JsonPropertyName("clubId")] public Guid? ClubId { get; set; }
    [JsonPropertyName("question")] public string Question { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("team")] public string? Team { get; set; }

    /// <summary>"session" answers strictly from PDFs uploaded to this chat; default "team".</summary>
    [JsonPropertyName("pdfScope")] public string? PdfScope { get; set; }
}

/// <summary>Body forwarded to the microservice (snake_case wire, matches AskRequest).</summary>
internal sealed class MicroserviceAskRequest
{
    [JsonPropertyName("question")] public string Question { get; set; } = string.Empty;

    // Omit when null so the microservice keeps its own defaults (team defaults to "EGY",
    // which is a non-nullable field — sending null would fail its validation).
    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; set; }

    [JsonPropertyName("team")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Team { get; set; }

    [JsonPropertyName("pdf_scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PdfScope { get; set; }
}

/// <summary>
/// Subset of the microservice's GET projects/{id}/status response we need to decide
/// whether a team was ever ingested (Phase 9.6 backfill gate). snake_case wire.
/// </summary>
internal sealed class MicroserviceProjectStatus
{
    [JsonPropertyName("pdf_count")] public int PdfCount { get; set; }
    [JsonPropertyName("has_box_score_csv")] public bool HasBoxScoreCsv { get; set; }
    [JsonPropertyName("has_chunks_csv")] public bool HasChunksCsv { get; set; }
    [JsonPropertyName("has_chroma_index")] public bool HasChromaIndex { get; set; }
    [JsonPropertyName("has_predictions_csv")] public bool HasPredictionsCsv { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}
