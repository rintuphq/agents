namespace RintUp.Agent.Kube;

/// <summary>
/// The workload that owns a pod: ReplicaSet -> Deployment, Job -> CronJob (otherwise the object itself). Pod names change on
/// every restart, so the finding is tied to the workload (3 pods of the same Deployment = one finding).
/// </summary>
internal sealed class Owners
{
    private readonly Dictionary<(string?, string), string?> _replicaSets = [];
    private readonly Dictionary<(string?, string), string?> _jobs = [];
    private readonly Dictionary<(string?, string), (string Kind, string Name)> _pods = [];

    public static Owners From(ClusterSnapshot snapshot)
    {
        var owners = new Owners();
        foreach (var rs in snapshot.ReplicaSets.Where(r => r.Metadata?.Name is not null))
        {
            owners._replicaSets[(rs.Metadata!.Namespace, rs.Metadata.Name!)] = Controller(rs.Metadata.OwnerReferences, "Deployment");
        }

        foreach (var job in snapshot.Jobs.Where(j => j.Metadata?.Name is not null))
        {
            owners._jobs[(job.Metadata!.Namespace, job.Metadata.Name!)] = Controller(job.Metadata.OwnerReferences, "CronJob");
        }

        foreach (var pod in snapshot.Pods.Where(p => p.Metadata?.Name is not null))
        {
            owners._pods[(pod.Metadata!.Namespace, pod.Metadata.Name!)] = owners.ForPod(pod.Metadata);
        }

        return owners;
    }

    public (string Kind, string Name) ForPod(ObjectMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var owner = meta.OwnerReferences?.FirstOrDefault(o => o.Controller == true) ?? (meta.OwnerReferences is [var any, ..] ? any : null);
        return owner switch
        {
            { Kind: "ReplicaSet", Name: { } rs } => _replicaSets.GetValueOrDefault((meta.Namespace, rs)) is { } deployment ? ("Deployment", deployment) : ("ReplicaSet", rs),
            { Kind: "Job", Name: { } job } => ForJob(meta.Namespace, job, null),
            { Kind: { } kind, Name: { } name } => (kind, name),
            _ => ("Pod", meta.Name ?? "unknown"),
        };
    }

    public (string Kind, string Name) ForJob(string? ns, string job, IReadOnlyList<OwnerReference>? references) =>
        (_jobs.GetValueOrDefault((ns, job)) ?? Controller(references, "CronJob")) is { } cron ? ("CronJob", cron) : ("Job", job);

    /// <summary>The object in the event: its workload if it is a pod, otherwise the object itself.</summary>
    public (string Kind, string Name) ForObject(ObjectReference reference) =>
        reference is { Kind: "Pod", Name: { } pod } && _pods.TryGetValue((reference.Namespace, pod), out var workload)
            ? workload
            : (reference.Kind ?? "Object", reference.Name ?? "unknown");

    private static string? Controller(IReadOnlyList<OwnerReference>? references, string kind) =>
        references?.FirstOrDefault(o => o.Kind == kind && o.Controller != false)?.Name;
}

/// <summary>Pod rules (CrashLoopBackOff, OOMKilled, image cannot be pulled, long Pending) and noise-filtered Warning events.</summary>
internal static class PodRules
{
    public static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ImagePullFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OomWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan EventWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Noise filter: only actionable Warning reasons. Frequent and transient ones such as BackOff, Unhealthy (probe) and
    /// FailedScheduling are either already covered by separate rules (CrashLoopBackOff, Pending) or are noise.
    /// </summary>
    public static readonly IReadOnlySet<string> EventReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "FailedMount", "FailedAttachVolume", "FailedCreatePodSandBox", "FailedCreate", "Evicted", "EvictionThresholdMet", "SystemOOM",
        "OOMKilling", "FreeDiskSpaceFailed", "ImageGCFailed", "ContainerGCFailed", "ProvisioningFailed", "VolumeResizeFailed",
        "FailedBinding", "FailedKillPod", "NetworkNotReady", "Rebooted",
    };

    private static readonly HashSet<string> s_pullReasons = new(StringComparer.Ordinal) { "ImagePullBackOff", "ErrImagePull", "InvalidImageName" };

    public static IEnumerable<Observation> Pods(IReadOnlyList<Pod> pods, Owners owners, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var observed = new List<(string Kind, string Name, Observation Observation)>();
        foreach (var pod in pods.Where(p => p.Metadata is { Name: not null, DeletionTimestamp: null }))
        {
            var (kind, name) = owners.ForPod(pod.Metadata!);
            observed.AddRange(ForPod(pod, now).Select(o => (kind, name, o)));
        }

        // Same workload + reason is one finding: earliest start, and the number of affected pods in the message.
        return observed.GroupBy(x => (x.Kind, x.Name, x.Observation.Finding.Namespace, x.Observation.Finding.Reason)).Select(g =>
        {
            var first = g.First().Observation;
            var since = g.Min(x => x.Observation.Finding.Since);
            var message = g.Count() == 1 ? first.Finding.Message : $"{g.Count()} pods: {first.Finding.Message}";
            return first with { Finding = first.Finding with { Kind = g.Key.Kind, Name = g.Key.Name, Since = since, Message = FindingDetector.Trim(message, first.Finding.Reason) } };
        });
    }

    public static IEnumerable<Observation> Events(IReadOnlyList<KubeEvent> events, Owners owners, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owners);
        return events
            .Where(e => e.Type == "Warning" && e.Reason is { } r && EventReasons.Contains(r) && e.InvolvedObject is not null)
            .Where(e => (e.Series?.LastObservedTime ?? e.LastTimestamp ?? e.EventTime ?? e.Metadata?.CreationTimestamp) >= now - EventWindow)
            .Select(e =>
            {
                var (kind, name) = owners.ForObject(e.InvolvedObject!);
                var finding = new Finding(e.InvolvedObject!.Namespace, kind, name, e.Reason!, Severity.Warning, FindingDetector.Trim(e.Message, e.Reason!), null);
                return new Observation(finding, TimeSpan.Zero);
            });
    }

    private static IEnumerable<Observation> ForPod(Pod pod, DateTimeOffset now)
    {
        var ns = pod.Metadata!.Namespace;
        var statuses = (pod.Status?.InitContainerStatuses ?? []).Concat(pod.Status?.ContainerStatuses ?? []).ToList();
        var crash = statuses.FirstOrDefault(c => c.State?.Waiting?.Reason == "CrashLoopBackOff");
        if (crash is not null)
        {
            var message = $"container {crash.Name} is restarting ({crash.RestartCount} restarts)";
            yield return new(new Finding(ns, "Pod", pod.Metadata.Name!, "CrashLoopBackOff", Severity.Error, message, null), TimeSpan.Zero);
        }

        var oom = statuses.Select(c => (c.Name, Terminated: c.State?.Terminated?.Reason == "OOMKilled" ? c.State.Terminated : c.LastState?.Terminated))
            .FirstOrDefault(x => x.Terminated is { Reason: "OOMKilled", FinishedAt: { } at } && at >= now - OomWindow);
        if (oom.Terminated is not null)
        {
            var message = $"container {oom.Name} was killed: out of memory (OOMKilled)";
            yield return new(new Finding(ns, "Pod", pod.Metadata.Name!, "OOMKilled", Severity.Error, message, oom.Terminated.FinishedAt), TimeSpan.Zero);
        }

        var pull = statuses.FirstOrDefault(c => c.State?.Waiting?.Reason is { } r && s_pullReasons.Contains(r));
        if (pull is not null)
        {
            var message = FindingDetector.Trim(pull.State!.Waiting!.Message, $"container {pull.Name} cannot pull its image");
            yield return new(new Finding(ns, "Pod", pod.Metadata.Name!, "ImagePullBackOff", Severity.Warning, message, null), ImagePullFor);
        }
        else if (crash is null && pod.Status?.Phase == "Pending" && pod.Metadata.CreationTimestamp is { } created && created <= now)
        {
            var message = FindingDetector.Trim(pod.Status.Reason, "pod has been Pending (not scheduled or not started)");
            yield return new(new Finding(ns, "Pod", pod.Metadata.Name!, "Pending", Severity.Warning, message, created), PendingFor);
        }
    }
}
