using System.Text.Json.Serialization;

namespace SportsPlatform.Auth.Core.Interfaces;

/// <summary>
/// Notifies the "Ask Equipo" chatbot / prediction microservice that a match's
/// stats PDFs changed, so it can (re)ingest them and retrain. The app does NOT
/// push file bytes; it sends signed pull-URLs the microservice fetches back.
/// Dispatch is best-effort and sport-gated (basketball only) at the call site.
/// </summary>
public interface IChatbotWebhookDispatcher
{
    /// <summary>
    /// Fire a "match stats updated" webhook for one match. Never throws — failures
    /// are logged and swallowed so a microservice outage can't break uploads.
    /// </summary>
    /// <param name="payload">The match identifiers, canonical box-score text and pull-URLs.</param>
    Task DispatchMatchStatsUpdatedAsync(MatchStatsWebhookPayload payload, CancellationToken ct = default);
}

/// <summary>
/// Body posted to the microservice. project_id on its side == TeamId here.
/// Wire format is snake_case (set explicitly) so the Python/pydantic side needs
/// no field aliases and is robust across pydantic v1/v2.
/// </summary>
public sealed class MatchStatsWebhookPayload
{
    [JsonPropertyName("team_id")]
    public Guid TeamId { get; init; }

    [JsonPropertyName("event_id")]
    public Guid EventId { get; init; }

    [JsonPropertyName("match_stats_id")]
    public Guid MatchStatsId { get; init; }

    /// <summary>Canonical box-score text the app already extracted (microservice trusts this verbatim).</summary>
    [JsonPropertyName("box_score_text")]
    public string? BoxScoreText { get; init; }

    /// <summary>One entry per stored PDF type with the URL the microservice pulls it from.</summary>
    [JsonPropertyName("documents")]
    public IReadOnlyList<WebhookDocumentRef> Documents { get; init; } = new List<WebhookDocumentRef>();
}

public sealed class WebhookDocumentRef
{
    [JsonPropertyName("pdf_type")]
    public string PdfType { get; init; } = string.Empty;

    [JsonPropertyName("pull_url")]
    public string PullUrl { get; init; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;
}
