using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Infrastructure.Services;

/// <summary>
/// Posts a signed "match stats updated" webhook to the chatbot microservice.
/// The chatbot forwards model work to the separate prediction service. Auth is a
/// shared bearer service token (Microservice:ServiceToken)
/// over a trusted network; mTLS is deferred. All failures are logged and swallowed.
/// </summary>
public class ChatbotWebhookDispatcher : IChatbotWebhookDispatcher
{
    public const string HttpClientName = "ChatbotMicroservice";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatbotWebhookDispatcher> _logger;

    public ChatbotWebhookDispatcher(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<ChatbotWebhookDispatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task DispatchMatchStatsUpdatedAsync(MatchStatsWebhookPayload payload, CancellationToken ct = default)
    {
        // Feature flag: skip cleanly when the microservice isn't configured/enabled.
        // Parse the raw value (avoids a dependency on Configuration.Binder's GetValue<T>).
        var enabled = bool.TryParse(_config["Microservice:Enabled"], out var e) && e;
        if (!enabled)
        {
            _logger.LogDebug("Chatbot webhook skipped: Microservice:Enabled is false.");
            return;
        }

        var baseUrl = _config["Microservice:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning("Chatbot webhook skipped: Microservice:BaseUrl is not configured.");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            // Relative path against the configured BaseUrl.
            var response = await client.PostAsJsonAsync("webhooks/match-stats-updated", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Chatbot webhook for match {MatchStatsId} returned {Status}.",
                    payload.MatchStatsId, (int)response.StatusCode);
                return;
            }

            _logger.LogInformation(
                "Chatbot webhook dispatched for match {MatchStatsId} (team {TeamId}, {DocCount} docs).",
                payload.MatchStatsId, payload.TeamId, payload.Documents.Count);
        }
        catch (Exception ex)
        {
            // Never let a microservice outage break the upload path.
            _logger.LogWarning(ex, "Chatbot webhook for match {MatchStatsId} failed.", payload.MatchStatsId);
        }
    }
}
