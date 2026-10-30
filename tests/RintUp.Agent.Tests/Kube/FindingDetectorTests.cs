using RintUp.Agent.Kube;
using static RintUp.Agent.Tests.Kube.KubeSamples;

namespace RintUp.Agent.Tests.Kube;

/// <summary>Finding detection with recorded API responses: CrashLoop, OOMKilled, NotReady, Pending, missing replicas, Job, PVC, event filter, durations.</summary>
public class FindingDetectorTests
{
    private static Dictionary<string, Finding> Emit(FindingTracker tracker, DateTimeOffset at) =>
        tracker.Update(FindingDetector.Detect(Snapshot(), at), at).ToDictionary(f => f.Key, StringComparer.Ordinal);

    [Fact]
    public void Findings_with_elapsed_duration_are_emitted_in_the_first_round()
    {
        var findings = Emit(new FindingTracker(), Now);

        Assert.Equal(
            [
                "-/Node/node-a/FreeDiskSpaceFailed", "-/Node/node-b/NotReady", "batch/Pod/report-gen/Pending", "data/PersistentVolumeClaim/data-db-2/Pending",
                "ops/CronJob/backup/JobFailed", "shop/Deployment/checkout/CrashLoopBackOff", "shop/Deployment/checkout/FailedMount", "shop/StatefulSet/worker/OOMKilled",
            ],
            findings.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CrashLoop_is_tied_to_the_workload_and_pods_are_merged_deleted_pod_is_not_counted()
    {
        var crash = Emit(new FindingTracker(), Now)["shop/Deployment/checkout/CrashLoopBackOff"];

        Assert.Equal((Severity.Error, "shop"), (crash.Severity, crash.Namespace));
        Assert.StartsWith("2 pods: container api is restarting", crash.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Emit(new FindingTracker(), Now).Keys, k => k.Contains("doomed", StringComparison.Ordinal));
    }

    [Fact]
    public void OOMKilled_only_within_last_15_minutes_NotReady_is_critical_with_transition_time()
    {
        var findings = Emit(new FindingTracker(), Now);
        var oom = findings["shop/StatefulSet/worker/OOMKilled"];
        var node = findings["-/Node/node-b/NotReady"];

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 11, 54, 30, TimeSpan.Zero), oom.Since);
        Assert.Equal((Severity.Critical, "Kubelet stopped posting node status."), (node.Severity, node.Message));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 11, 50, 0, TimeSpan.Zero), node.Since);
        Assert.DoesNotContain(Emit(new FindingTracker(), Now.AddMinutes(20)).Keys, k => k.EndsWith("/OOMKilled", StringComparison.Ordinal));
    }

    [Fact]
    public void Node_being_deleted_by_autoscaler_and_short_lived_conditions_are_not_findings()
    {
        var findings = Emit(new FindingTracker(), Now);

        Assert.DoesNotContain("-/Node/node-c/NotReady", findings.Keys);
        Assert.DoesNotContain("-/Node/node-d/NotReady", findings.Keys);
        Assert.DoesNotContain("batch/Pod/fresh/Pending", findings.Keys);
        Assert.DoesNotContain(findings.Keys, k => k.Contains("Unhealthy", StringComparison.Ordinal) || k.Contains("ProvisioningFailed", StringComparison.Ordinal));
        Assert.DoesNotContain(findings.Keys, k => k.Contains("migrate-old", StringComparison.Ordinal) || k.Contains("seed", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_replicas_and_image_pull_become_findings_after_5_minutes()
    {
        var tracker = new FindingTracker();
        var first = Emit(tracker, Now);
        var later = Emit(tracker, Now.AddMinutes(5));

        Assert.DoesNotContain("shop/Deployment/checkout/ReplicasUnavailable", first.Keys);
        Assert.Equal(Severity.Warning, later["shop/Deployment/checkout/ReplicasUnavailable"].Severity);
        Assert.Equal("1/3 replicas available", later["shop/Deployment/checkout/ReplicasUnavailable"].Message);
        Assert.Equal(Severity.Error, later["shop/Deployment/web/ReplicasUnavailable"].Severity);
        Assert.Contains("data/StatefulSet/db/ReplicasUnavailable", later.Keys);
        Assert.Contains("shop/Deployment/web/ImagePullBackOff", later.Keys);
        Assert.Contains("-/Node/node-d/NotReady", later.Keys);
        Assert.DoesNotContain(later.Keys, k => k.Contains("/api/", StringComparison.Ordinal) || k.Contains("idle", StringComparison.Ordinal));
        Assert.DoesNotContain("shop/Pod/web-5c6d7-xyz12/Pending", later.Keys);
    }

    [Fact]
    public void When_condition_clears_tracking_is_forgotten_and_duration_starts_over()
    {
        var tracker = new FindingTracker();
        Emit(tracker, Now);
        tracker.Update(FindingDetector.Detect(ClusterSnapshot.Empty, Now.AddMinutes(1)), Now.AddMinutes(1));

        Assert.DoesNotContain("shop/Deployment/checkout/ReplicasUnavailable", Emit(tracker, Now.AddMinutes(5)).Keys);
        Assert.Empty(tracker.Update(FindingDetector.Detect(ClusterSnapshot.Empty, Now), Now));
    }

    [Fact]
    public void Ready_count_and_findings_limit()
    {
        var snapshot = Snapshot();
        var pods = Enumerable.Range(0, 600).Select(i => new Pod(new ObjectMeta($"p{i}", "ns"), new PodStatus("Running", null,
            [new ContainerStatus("c", 5, new ContainerState(new ContainerStateWaiting("CrashLoopBackOff", null), null), null)], null))).ToList();

        Assert.Equal([true, false, false, false], snapshot.Nodes.Select(FindingDetector.IsReady));
        Assert.Equal(FindingDetector.MaxFindings, FindingDetector.Detect(ClusterSnapshot.Empty with { Pods = pods }, Now).Count);
    }
}
