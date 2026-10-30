using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RintUp.Agent.Kube;
using RintUp.Agent.Tests.Support;

namespace RintUp.Agent.Tests.Kube;

/// <summary>
/// Cluster agent end to end (fake Kubernetes API + fake RintUp): of two replicas only the leader sends reports, when the leader
/// stops the other takes over, no report is sent if a list cannot be read, report format and auth headers are correct, key/token are not logged.
/// </summary>
public sealed class ClusterRunnerTests : IDisposable
{
    private const string Key = "rka_0123456789abcdefghijABCDEFGHIJ0123456789abc";
    private const string ServiceAccountToken = "eyJhbGciOiJSUzI1NiJ9.sa-token.sig";

    private readonly string _tokenFile = Path.GetTempFileName();
    private readonly FakeTimeProvider _time = new(KubeSamples.Now);
    private readonly FakeKubeApi _api = new();
    private readonly ScriptedHandler _rintup = new() { Default = () => ScriptedHandler.Json(System.Net.HttpStatusCode.OK, """{"findings":8,"opened":8,"resolved":0}""") };
    private readonly StringWriter _logs = new();

    public ClusterRunnerTests() => File.WriteAllText(_tokenFile, ServiceAccountToken + "\n");

    public void Dispose()
    {
        File.Delete(_tokenFile);
        _api.Dispose();
        _rintup.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Of_two_replicas_only_the_leader_sends_and_the_other_takes_over_when_it_stops()
    {
        var (a, b) = (Runner("agent-a"), Runner("agent-b"));

        var sentA = await a.RunOnceAsync(Ct);
        var sentB = await b.RunOnceAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(10));
        var renewA = await a.RunOnceAsync(Ct);
        var followB = await b.RunOnceAsync(Ct);
        var leaderStill = _api.CurrentLease!.Spec!.HolderIdentity;

        // A stopped (not renewing): B takes over 30 s after the last renewal at 12:00:10.
        _time.Advance(TimeSpan.FromSeconds(35));
        var takeover = await b.RunOnceAsync(Ct);

        Assert.Equal(8, sentA);
        Assert.Null(sentB);
        Assert.Null(renewA);
        Assert.Null(followB);
        Assert.Equal("agent-a", leaderStill);
        Assert.Equal(8, takeover);
        Assert.Equal(("agent-b", 1), (_api.CurrentLease!.Spec!.HolderIdentity, _api.CurrentLease.Spec.LeaseTransitions));
        Assert.Equal(2, _rintup.Requests.Count);
    }

    [Fact]
    public async Task Report_format_auth_and_paging_parameters()
    {
        await Runner("agent-a").RunOnceAsync(Ct);

        var (authorization, body, uri) = Assert.Single(_rintup.Requests);
        using var report = JsonDocument.Parse(body);
        var root = report.RootElement;
        Assert.Equal("Bearer " + Key, authorization);
        Assert.Equal("https://rintup.example/api/v1/agent/kubernetes", uri!.ToString());
        Assert.Equal(("v1.31.2", AgentVersion.Current), (root.GetProperty("kubernetesVersion").GetString(), root.GetProperty("agentVersion").GetString()));
        Assert.Equal(4, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal(8, root.GetProperty("findings").GetArrayLength());
        var crash = root.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("reason").GetString() == "CrashLoopBackOff");
        Assert.Equal(("shop", "Deployment", "checkout", "error"), (crash.GetProperty("namespace").GetString(), crash.GetProperty("kind").GetString(), crash.GetProperty("name").GetString(), crash.GetProperty("severity").GetString()));
        Assert.All(_api.Requests, r => Assert.Equal("Bearer " + ServiceAccountToken, r.Authorization));
        Assert.All(_api.Requests.Where(r => r.Method == HttpMethod.Get && !r.PathAndQuery.Contains("lease", StringComparison.Ordinal) && r.PathAndQuery != "/version"),
            r => Assert.Contains("limit=500", r.PathAndQuery, StringComparison.Ordinal));
        Assert.DoesNotContain(_api.Requests, r => r.Method != HttpMethod.Get && !r.PathAndQuery.Contains("/leases", StringComparison.Ordinal));
        Assert.DoesNotContain(_api.Requests, r => r.PathAndQuery.Contains("secrets", StringComparison.Ordinal));
    }

    /// <summary>
    /// A new leader (or a restarted agent) sends observations whose duration condition has not been met yet as "pending";
    /// the server does not resolve their open alerts (an incident is not closed and reopened on a leader change).
    /// </summary>
    [Fact]
    public async Task New_leader_sends_observations_with_unmet_duration_as_pending()
    {
        await Runner("agent-a").RunOnceAsync(Ct);

        var (_, body, _) = Assert.Single(_rintup.Requests);
        using var report = JsonDocument.Parse(body);
        var pending = report.RootElement.GetProperty("pending").EnumerateArray()
            .Select(p => $"{p.GetProperty("namespace").GetString() ?? "-"}/{p.GetProperty("kind").GetString()}/{p.GetProperty("name").GetString()}/{p.GetProperty("reason").GetString()}")
            .ToList();
        var findings = report.RootElement.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("reason").GetString()).ToList();
        Assert.Contains("shop/Deployment/checkout/ReplicasUnavailable", pending);
        Assert.Contains("shop/Deployment/web/ImagePullBackOff", pending);
        Assert.DoesNotContain("ReplicasUnavailable", findings);
        Assert.DoesNotContain(pending, k => k.EndsWith("/CrashLoopBackOff", StringComparison.Ordinal));
    }

    [Fact]
    public async Task If_a_list_cannot_be_read_no_partial_report_is_sent_and_key_is_not_logged()
    {
        _api.FailPods = true;
        var runner = Runner("agent-a");

        await Assert.ThrowsAsync<KubeApiException>(() => runner.RunOnceAsync(Ct));

        Assert.Empty(_rintup.Requests);
        Assert.DoesNotContain(Key, _logs.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ServiceAccountToken, _logs.ToString(), StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClusterRunner Runner(string identity)
    {
        var (settings, error) = AgentSettings.Load(null, k => k switch
        {
            "RINTUP_TOKEN" => Key,
            "RINTUP_URL" => "https://rintup.example",
            _ => null,
        }, requireNode: false);
        Assert.Null(error);
        var config = new KubeConfig(new Uri("https://10.96.0.1:443"), _tokenFile, "/unused/ca.crt", "rintup", identity);
        var kube = new KubeClient(new HttpClient(_api, disposeHandler: false) { BaseAddress = config.ApiBase }, config, _time);
        var elector = new LeaderElector(kube, (config.Namespace, LeaderElector.LeaseName, identity), _time);
        var sender = new ClusterSender(AgentHttp.Create(_rintup), settings!);
        return new ClusterRunner((kube, elector, sender), (_time, new AgentLog(_logs)));
    }
}
