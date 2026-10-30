using System.Text.Json;
using RintUp.Agent.Kube;
using static RintUp.Agent.Tests.Kube.KubeSamples;

namespace RintUp.Agent.Tests.Kube;

/// <summary>Leader election via a Lease: create, renew, follow, take over on expiry; MicroTime format.</summary>
public class LeaseDecisionTests
{
    private const string Holder = "rintup-agent-cluster-6b9c-aaaaa";
    private const string Other = "rintup-agent-cluster-6b9c-bbbbb";

    private static Lease Recorded() => JsonSerializer.Deserialize(Text("lease.json"), KubeJsonContext.Default.Lease)!;

    [Fact]
    public void Lease_is_created_if_missing()
    {
        var (action, next) = LeaseDecision.Decide(null, ("rintup", "rintup-cluster-agent", Other), Now, 30);

        Assert.Equal(LeaseAction.Create, action);
        Assert.Equal((Other, 30, "2026-10-30T12:00:00.000000Z", 0), (next.Spec!.HolderIdentity, next.Spec.LeaseDurationSeconds, next.Spec.RenewTime, next.Spec.LeaseTransitions));
        Assert.Equal(("rintup", "rintup-cluster-agent", "coordination.k8s.io/v1", "Lease"), (next.Metadata!.Namespace, next.Metadata.Name, next.ApiVersion, next.Kind));
        var json = JsonSerializer.Serialize(next, KubeJsonContext.Default.Lease);
        Assert.DoesNotContain("resourceVersion", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Follows_if_someone_else_is_leader_and_not_expired()
    {
        var (action, _) = LeaseDecision.Decide(Recorded(), ("rintup", "rintup-cluster-agent", Other), Now, 30);

        Assert.Equal(LeaseAction.Follow, action);
    }

    [Fact]
    public void Leader_renews_its_own_lease_with_resourceVersion()
    {
        var (action, next) = LeaseDecision.Decide(Recorded(), ("rintup", "rintup-cluster-agent", Holder), Now.AddSeconds(5), 30);

        Assert.Equal(LeaseAction.Renew, action);
        Assert.Equal(("88123", "2026-10-30T12:00:05.000000Z", 2), (next.Metadata!.ResourceVersion, next.Spec!.RenewTime, next.Spec.LeaseTransitions));
        Assert.Contains("\"resourceVersion\":\"88123\"", JsonSerializer.Serialize(next, KubeJsonContext.Default.Lease), StringComparison.Ordinal);
    }

    [Fact]
    public void Expired_lease_is_taken_over_and_transitions_increment()
    {
        // After renewTime 11:59:50.123456 + 30 s = 12:00:20.123456.
        var (early, _) = LeaseDecision.Decide(Recorded(), ("rintup", "rintup-cluster-agent", Other), Now.AddSeconds(20), 30);
        var (action, next) = LeaseDecision.Decide(Recorded(), ("rintup", "rintup-cluster-agent", Other), Now.AddSeconds(21), 30);

        Assert.Equal(LeaseAction.Follow, early);
        Assert.Equal(LeaseAction.TakeOver, action);
        Assert.Equal((Other, 3, "88123"), (next.Spec!.HolderIdentity, next.Spec.LeaseTransitions, next.Metadata!.ResourceVersion));
        Assert.Equal(next.Spec.AcquireTime, next.Spec.RenewTime);
    }

    [Fact]
    public void Lease_without_holder_is_taken_over()
    {
        var empty = Recorded() with { Spec = Recorded().Spec! with { HolderIdentity = "" } };

        Assert.Equal(LeaseAction.TakeOver, LeaseDecision.Decide(empty, ("rintup", "rintup-cluster-agent", Other), Now, 30).Action);
    }
}
