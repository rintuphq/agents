using System.Collections.Concurrent;
using System.Net;
using RintUp.Agent.Proc;

namespace RintUp.Agent.Tests.Support;

/// <summary>Sample /proc files (Samples/) and a fake system source that can be modified during a test.</summary>
internal sealed class FakeSystemSource : ISystemSource
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public List<int> Pids { get; } = [];

    public Dictionary<string, (long Total, long Used)> Disks { get; } = new(StringComparer.Ordinal);

    public static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    public static FakeSystemSource WithSamples()
    {
        var source = new FakeSystemSource();
        foreach (var (key, file) in new[] { ("stat", "stat"), ("meminfo", "meminfo"), ("loadavg", "loadavg"), ("uptime", "uptime"), ("net/dev", "net-dev"), ("mounts", "mounts") })
        {
            source.Files[key] = Sample(file);
        }

        source.Disks["/"] = (100_000, 25_000);
        source.Disks["/boot/efi"] = (500, 100);
        return source;
    }

    public string? Read(string path) => Files.GetValueOrDefault(path);

    public IReadOnlyList<int> ProcessIds() => Pids;

    public (long Total, long Used)? DiskSpace(string mount) => Disks.TryGetValue(mount, out var space) ? space : null;

    public string? Hostname() => "test-host";
}

/// <summary>Fake HTTP: records requests (body + Authorization) and returns the next queued response or exception.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    public ConcurrentQueue<(string? Authorization, string Body, Uri? Uri)> Requests { get; } = new();

    public Queue<Func<HttpResponseMessage>> Responses { get; } = new();

    public Func<HttpResponseMessage> Default { get; set; } = () => Json(HttpStatusCode.OK, """{"accepted":1,"watch":[]}""");

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static Func<HttpResponseMessage> Fail() => () => throw new HttpRequestException("no network");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.Headers.Authorization?.ToString(), body, request.RequestUri));
        return (Responses.Count > 0 ? Responses.Dequeue() : Default)();
    }
}
