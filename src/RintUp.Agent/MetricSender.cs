using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RintUp.Agent.Wire;

namespace RintUp.Agent;

/// <summary>Send outcome. Sent: the buffer is cleared. Retry: the buffer is kept. Drop: the server will not accept it, discard.</summary>
internal enum SendOutcome
{
    Sent = 0,
    Retry = 1,
    Drop = 2,
    Unauthorized = 3,
}

/// <summary>
/// HTTPS POST to RintUp (Authorization: Bearer srv_...). Redirects are not followed (so the token is not carried to another
/// address). Error logs contain only the status code or exception type; the token/body is never written.
/// </summary>
internal sealed class MetricSender(HttpClient http, AgentSettings settings, AgentLog log)
{
    public async Task<(SendOutcome Outcome, IReadOnlyList<string>? Watch)> SendAsync(MetricsPayload payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = JsonContent.Create(payload, AgentJsonContext.Default.MetricsPayload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            return await HandleAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            log.Warning($"Send failed ({ex.GetType().Name}); samples are kept in the buffer");
            return (SendOutcome.Retry, null);
        }
    }

    private async Task<(SendOutcome, IReadOnlyList<string>?)> HandleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return (SendOutcome.Sent, await ReadWatchAsync(response, cancellationToken));
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            log.Error($"Token rejected (HTTP {status}): the token may have been revoked or regenerated");
            return (SendOutcome.Unauthorized, null);
        }

        // 408/409/429 and 5xx are transient; other 4xx (malformed/oversized body) would not be accepted even if retried.
        var transient = status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests;
        log.Warning($"Server did not accept the samples (HTTP {status}){(transient ? "; will retry" : "; samples discarded")}");
        return (transient ? SendOutcome.Retry : SendOutcome.Drop, null);
    }

    private static async Task<IReadOnlyList<string>?> ReadWatchAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var ack = await response.Content.ReadFromJsonAsync(AgentJsonContext.Default.MetricsAck, cancellationToken);
            return ack?.Watch;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
