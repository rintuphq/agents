namespace RintUp.Agent.Kube;

/// <summary>Cluster objects read in one round (read-only list calls).</summary>
internal sealed record ClusterSnapshot(
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Pod> Pods,
    IReadOnlyList<Deployment> Deployments,
    IReadOnlyList<StatefulSet> StatefulSets,
    IReadOnlyList<DaemonSet> DaemonSets,
    IReadOnlyList<ReplicaSet> ReplicaSets,
    IReadOnlyList<Job> Jobs,
    IReadOnlyList<KubeEvent> Events,
    IReadOnlyList<PersistentVolumeClaim> Claims)
{
    public static ClusterSnapshot Empty { get; } = new([], [], [], [], [], [], [], [], []);
}

/// <summary>Observation: the condition is true right now. Once it lasts for <see cref="For"/> it becomes a finding (<see cref="FindingTracker"/>).</summary>
internal sealed record Observation(Finding Finding, TimeSpan For);

/// <summary>
/// Pure finding detection (no network; tests call it with recorded JSON samples). Rules:
/// node NotReady (3 min; except nodes the autoscaler is deleting), Deployment/StatefulSet/DaemonSet missing replicas (5 min),
/// Job failure (last 1 h), PVC Pending (10 min) / Lost; pod rules and Warning events are in <see cref="PodRules"/>.
/// </summary>
internal static class FindingDetector
{
    public const int MaxFindings = 500;
    public const int MaxMessageLength = 300;

    public static readonly TimeSpan NodeNotReadyFor = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ReplicasFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan JobWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan ClaimPendingFor = TimeSpan.FromMinutes(10);

    private const string AutoscalerTaint = "ToBeDeletedByClusterAutoscaler";

    public static IReadOnlyList<Observation> Detect(ClusterSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var owners = Owners.From(snapshot);
        return Nodes(snapshot.Nodes)
            .Concat(PodRules.Pods(snapshot.Pods, owners, now))
            .Concat(Workloads(snapshot))
            .Concat(Jobs(snapshot.Jobs, owners, now))
            .Concat(Claims(snapshot.Claims, now))
            .Concat(PodRules.Events(snapshot.Events, owners, now))
            .GroupBy(o => o.Finding.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(o => Severity.Rank(o.Finding.Severity)).ThenBy(o => o.Finding.Key, StringComparer.Ordinal)
            .Take(MaxFindings)
            .ToList();
    }

    /// <summary>Whether the node is Ready (not Ready means False or Unknown).</summary>
    public static bool IsReady(Node node) =>
        node.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready") is not { } ready || ready.Status == "True";

    internal static string Trim(string? message, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
        return text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;
    }

    private static IEnumerable<Observation> Nodes(IReadOnlyList<Node> nodes)
    {
        foreach (var node in nodes)
        {
            var leaving = node.Metadata?.DeletionTimestamp is not null || node.Spec?.Taints?.Any(t => t.Key == AutoscalerTaint) == true;
            var ready = node.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready");
            if (leaving || node.Metadata?.Name is not { } name || ready is null || ready.Status == "True")
            {
                continue;
            }

            var message = Trim(ready.Message, $"Node {name} is not ready ({ready.Status})");
            yield return new(new Finding(null, "Node", name, "NotReady", Severity.Critical, message, ready.LastTransitionTime), NodeNotReadyFor);
        }
    }

    private static IEnumerable<Observation> Workloads(ClusterSnapshot snapshot)
    {
        foreach (var d in snapshot.Deployments)
        {
            if (Shortfall(d.Metadata, d.Spec?.Replicas ?? 1, d.Status?.AvailableReplicas ?? 0, "Deployment") is { } o)
            {
                yield return o;
            }
        }

        foreach (var s in snapshot.StatefulSets)
        {
            if (Shortfall(s.Metadata, s.Spec?.Replicas ?? 1, s.Status?.ReadyReplicas ?? 0, "StatefulSet") is { } o)
            {
                yield return o;
            }
        }

        foreach (var ds in snapshot.DaemonSets)
        {
            var ready = ds.Status?.NumberAvailable ?? ds.Status?.NumberReady ?? 0;
            if (Shortfall(ds.Metadata, ds.Status?.DesiredNumberScheduled ?? 0, ready, "DaemonSet") is { } o)
            {
                yield return o;
            }
        }
    }

    private static Observation? Shortfall(ObjectMeta? meta, int desired, int available, string kind)
    {
        if (desired <= 0 || available >= desired || meta?.Name is not { } name || meta.DeletionTimestamp is not null)
        {
            return null;
        }

        var severity = available == 0 ? Severity.Error : Severity.Warning;
        var message = $"{available}/{desired} replicas available";
        return new(new Finding(meta.Namespace, kind, name, "ReplicasUnavailable", severity, message, null), ReplicasFor);
    }

    private static IEnumerable<Observation> Jobs(IReadOnlyList<Job> jobs, Owners owners, DateTimeOffset now)
    {
        foreach (var job in jobs)
        {
            var failed = job.Status?.Conditions?.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
            if (failed is null || job.Metadata?.Name is not { } name || failed.LastTransitionTime < now - JobWindow)
            {
                continue;
            }

            var (kind, workload) = owners.ForJob(job.Metadata.Namespace, name, job.Metadata.OwnerReferences);
            var message = Trim($"Job {name} failed: {failed.Reason} {failed.Message}", $"Job {name} failed");
            yield return new(new Finding(job.Metadata.Namespace, kind, workload, "JobFailed", Severity.Error, message, failed.LastTransitionTime), TimeSpan.Zero);
        }
    }

    private static IEnumerable<Observation> Claims(IReadOnlyList<PersistentVolumeClaim> claims, DateTimeOffset now)
    {
        foreach (var claim in claims)
        {
            var meta = claim.Metadata;
            if (meta?.Name is not { } name || meta.DeletionTimestamp is not null)
            {
                continue;
            }

            if (claim.Status?.Phase == "Lost")
            {
                yield return new(new Finding(meta.Namespace, "PersistentVolumeClaim", name, "Lost", Severity.Error, "The bound volume is lost", null), TimeSpan.Zero);
            }
            else if (claim.Status?.Phase == "Pending" && meta.CreationTimestamp is { } created && created <= now)
            {
                yield return new(new Finding(meta.Namespace, "PersistentVolumeClaim", name, "Pending", Severity.Warning, "Volume claim is not bound", created), ClaimPendingFor);
            }
        }
    }
}
