using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace RintUp.Agent.Kube;

/// <summary>Leader election via a Lease: only the leader sends reports (no duplicate events with 2 replicas).</summary>
internal sealed class LeaderElector(KubeClient kube, (string Namespace, string Name, string Identity) lease, TimeProvider time)
{
    public const string LeaseName = "rintup-cluster-agent";
    public const int DurationSeconds = 30;

    public bool IsLeader { get; private set; }

    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var current = await kube.GetLeaseAsync(lease.Namespace, lease.Name, cancellationToken);
        var (action, next) = LeaseDecision.Decide(current, lease, time.GetUtcNow(), DurationSeconds);
        IsLeader = action switch
        {
            LeaseAction.Create => await kube.CreateLeaseAsync(next, cancellationToken) is not null,
            LeaseAction.Renew or LeaseAction.TakeOver => await kube.UpdateLeaseAsync(next, cancellationToken) is not null,
            _ => false,
        };
        return IsLeader;
    }
}

/// <summary>Reads all lists; if even one cannot be read the round is skipped (so an incomplete snapshot does not wrongly resolve open findings).</summary>
internal sealed class ClusterCollector(KubeClient kube)
{
    public async Task<ClusterSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var json = KubeJsonContext.Default;
        return new ClusterSnapshot(
            await kube.ListAsync("/api/v1/nodes", json.KubeListNode, cancellationToken),
            await kube.ListAsync("/api/v1/pods?fieldSelector=status.phase%21%3DSucceeded", json.KubeListPod, cancellationToken),
            await kube.ListAsync("/apis/apps/v1/deployments", json.KubeListDeployment, cancellationToken),
            await kube.ListAsync("/apis/apps/v1/statefulsets", json.KubeListStatefulSet, cancellationToken),
            await kube.ListAsync("/apis/apps/v1/daemonsets", json.KubeListDaemonSet, cancellationToken),
            await kube.ListAsync("/apis/apps/v1/replicasets", json.KubeListReplicaSet, cancellationToken),
            await kube.ListAsync("/apis/batch/v1/jobs", json.KubeListJob, cancellationToken),
            await kube.ListAsync("/api/v1/events?fieldSelector=type%3DWarning", json.KubeListKubeEvent, cancellationToken),
            await kube.ListAsync("/api/v1/persistentvolumeclaims", json.KubeListPersistentVolumeClaim, cancellationToken));
    }
}

/// <summary>Report to RintUp (Bearer rka_). Redirects are not followed; the key and body are not logged.</summary>
internal sealed class ClusterSender(HttpClient http, AgentSettings settings)
{
    public async Task<(SendOutcome Outcome, int Status)> SendAsync(ClusterReportPayload payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl + "/api/v1/agent/kubernetes", UriKind.Absolute))
        {
            Content = JsonContent.Create(payload, ClusterJsonContext.Default.ClusterReportPayload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode ? (SendOutcome.Sent, status)
                : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? (SendOutcome.Unauthorized, status)
                : (SendOutcome.Retry, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (SendOutcome.Retry, 0);
        }
    }
}

/// <summary>
/// Cluster agent loop: the Lease is renewed every <see cref="TickInterval"/>; every <see cref="ReportInterval"/> the leader reads
/// all lists, derives findings and sends the full snapshot. A replica that loses leadership becomes a follower (followers cannot send).
/// </summary>
internal sealed class ClusterRunner(
    (KubeClient Kube, LeaderElector Elector, ClusterSender Sender) parts, (TimeProvider Time, AgentLog Log) env)
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

    /// <summary>Maximum pending observations accepted by the server (same as the findings limit).</summary>
    public const int MaxPending = 500;

    private readonly FindingTracker _tracker = new();
    private readonly ClusterCollector _collector = new(parts.Kube);
    private DateTimeOffset? _lastReport;
    private bool _wasLeader;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval, env.Time);
        do
        {
            try
            {
                await RunOnceAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is KubeApiException or HttpRequestException or IOException or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                env.Log.Warning($"Kubernetes API round failed ({(ex is KubeApiException api ? "HTTP " + api.Status : ex.GetType().Name)}); will retry next round");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    /// <summary>Single round; the number of findings in the report if sent, otherwise null.</summary>
    public async Task<int?> RunOnceAsync(CancellationToken cancellationToken)
    {
        var leader = await parts.Elector.TickAsync(cancellationToken);
        if (leader != _wasLeader)
        {
            env.Log.Info(leader ? "Cluster agent became leader; reports will be sent from this replica" : "Cluster agent is a follower; another replica is leader");
            _tracker.Reset();
            _wasLeader = leader;
        }

        var now = env.Time.GetUtcNow();
        if (!leader || (_lastReport is { } last && now - last < ReportInterval))
        {
            return null;
        }

        var snapshot = await _collector.CollectAsync(cancellationToken);
        var findings = _tracker.Update(FindingDetector.Detect(snapshot, now), now);
        var nodes = snapshot.Nodes.Where(n => n.Metadata?.Name is not null).Select(n => new NodeState(n.Metadata!.Name!, FindingDetector.IsReady(n))).ToList();
        var version = await parts.Kube.VersionAsync(cancellationToken);
        var (outcome, status) = await parts.Sender.SendAsync(new ClusterReportPayload(AgentVersion.Current, version, nodes, findings, _tracker.Pending.Take(MaxPending).ToList()), cancellationToken);
        if (outcome != SendOutcome.Sent)
        {
            env.Log.Warning($"Cluster report could not be sent (HTTP {status}){(outcome == SendOutcome.Unauthorized ? ": cluster key is invalid or has been rotated" : "; will retry")}");
            return null;
        }

        _lastReport = now;
        return findings.Count;
    }
}
