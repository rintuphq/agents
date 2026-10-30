using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RintUp.Agent.Tests.Support;

namespace RintUp.Agent.Tests;

/// <summary>Buffering and resending (fake HTTP; no real network), wire format, watch list.</summary>
public sealed class AgentRunnerTests : IDisposable
{
    private const string Token = "srv_0123456789abcdefghijABCDEFGHIJ0123456789abc";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly ScriptedHandler _handler = new();
    private readonly StringWriter _log = new();
    private readonly HttpClient _http;
    private readonly AgentRunner _runner;

    public AgentRunnerTests()
    {
        var settings = AgentSettings.Load(null, key => key switch
        {
            "RINTUP_TOKEN" => Token,
            "RINTUP_URL" => "https://rintup.test",
            "RINTUP_PROCESSES" => "sshd",
            _ => null,
        }).Settings!;
        var log = new AgentLog(_log);
        _http = AgentHttp.Create(_handler);
        _runner = new AgentRunner(new MetricCollector(FakeSystemSource.WithSamples(), _time), new MetricSender(_http, settings, log), settings, (_time, log));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SendOutcome> TickAsync()
    {
        _time.Advance(TimeSpan.FromSeconds(60));
        return await _runner.RunOnceAsync("host", "Linux", Ct);
    }

    private JsonElement LastBody() => JsonDocument.Parse(_handler.Requests.Last().Body).RootElement;

    [Fact]
    public async Task Buffers_while_network_is_down_and_sends_in_bulk_when_reconnected()
    {
        _handler.Responses.Enqueue(ScriptedHandler.Fail());
        _handler.Responses.Enqueue(() => ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        _handler.Responses.Enqueue(() => ScriptedHandler.Json(HttpStatusCode.TooManyRequests, "{}"));

        Assert.Equal(SendOutcome.Retry, await TickAsync());
        Assert.Equal(SendOutcome.Retry, await TickAsync());
        Assert.Equal(SendOutcome.Retry, await TickAsync());
        Assert.Equal(3, _runner.Buffered);

        Assert.Equal(SendOutcome.Sent, await TickAsync());
        Assert.Equal(0, _runner.Buffered);
        Assert.Equal(4, LastBody().GetProperty("samples").GetArrayLength());
        Assert.Equal(Enumerable.Repeat("Bearer " + Token, 4), _handler.Requests.Select(r => r.Authorization));
        Assert.Equal("https://rintup.test/api/v1/agent/metrics", _handler.Requests.Last().Uri!.ToString());
    }

    [Fact]
    public async Task Buffer_holds_at_most_60_samples_and_drops_the_oldest()
    {
        _handler.Default = ScriptedHandler.Fail();
        for (var i = 0; i < 65; i++)
        {
            await TickAsync();
        }

        Assert.Equal(AgentRunner.MaxBuffered, _runner.Buffered);
        _handler.Default = () => ScriptedHandler.Json(HttpStatusCode.OK, """{"accepted":60}""");
        await TickAsync();
        var samples = LastBody().GetProperty("samples");
        Assert.Equal(60, samples.GetArrayLength());
        Assert.Equal(_time.GetUtcNow().AddMinutes(-59), samples[0].GetProperty("time").GetDateTimeOffset());
    }

    [Fact]
    public async Task Rejected_body_is_dropped_revoked_token_keeps_buffer_and_is_not_logged()
    {
        _handler.Responses.Enqueue(() => ScriptedHandler.Json(HttpStatusCode.BadRequest, "{}"));
        _handler.Responses.Enqueue(() => ScriptedHandler.Json(HttpStatusCode.Unauthorized, "{}"));

        Assert.Equal(SendOutcome.Drop, await TickAsync());
        Assert.Equal(0, _runner.Buffered);
        Assert.Equal(SendOutcome.Unauthorized, await TickAsync());
        Assert.Equal(1, _runner.Buffered);
        Assert.DoesNotContain(Token, _log.ToString(), StringComparison.Ordinal);
        Assert.Contains("<3>", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wire_format_carries_the_same_field_names_as_the_server_contract()
    {
        await TickAsync();
        var body = LastBody();
        var sample = body.GetProperty("samples")[0];

        Assert.Equal(AgentVersion.Current, body.GetProperty("agentVersion").GetString());
        Assert.Equal(("host", "Linux"), (body.GetProperty("hostname").GetString(), body.GetProperty("os").GetString()));
        Assert.Equal(_time.GetUtcNow(), body.GetProperty("sentAt").GetDateTimeOffset());
        foreach (var name in new[]
        {
            "time", "cpuPercent", "load1", "load5", "load15", "memTotalBytes", "memUsedBytes", "memAvailableBytes", "swapTotalBytes",
            "swapUsedBytes", "disks", "netRxBytesPerSecond", "netTxBytesPerSecond", "uptimeSeconds", "processCount", "processes",
        })
        {
            Assert.True(sample.TryGetProperty(name, out _), name);
        }

        var disk = sample.GetProperty("disks")[0];
        Assert.Equal(("/", 100_000, 25_000), (disk.GetProperty("mount").GetString(), disk.GetProperty("totalBytes").GetInt64(), disk.GetProperty("usedBytes").GetInt64()));
    }

    [Fact]
    public async Task Server_watch_list_merges_with_configuration_and_invalid_name_is_dropped()
    {
        _handler.Responses.Enqueue(() => ScriptedHandler.Json(HttpStatusCode.OK, """{"accepted":1,"watch":["nginx","bad name","sshd"]}"""));

        await TickAsync();
        Assert.Equal(["sshd", "nginx"], _runner.Watched);
        await TickAsync();

        var processes = LastBody().GetProperty("samples")[0].GetProperty("processes");
        Assert.Equal(["sshd", "nginx"], processes.EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
        _log.Dispose();
    }
}
