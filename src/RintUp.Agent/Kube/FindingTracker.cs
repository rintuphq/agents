using System.Globalization;

namespace RintUp.Agent.Kube;

/// <summary>
/// Ties observations to duration: once a condition lasts uninterrupted for <see cref="Observation.For"/> it becomes a finding. The start
/// is the object's own time (Since: node transition time, pod creation) if available; otherwise the round in which it was first seen
/// (reset when the leader changes, at most one duration of delay). A key that is no longer observed is forgotten (the condition cleared).
/// Observations whose duration has not elapsed yet stay in <see cref="Pending"/> and are sent as "pending" in the report: the server does
/// not resolve their open alerts (so a leader change or restart does not close findings without an object time and reopen them minutes later).
/// </summary>
internal sealed class FindingTracker
{
    private readonly Dictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);

    /// <summary>Observations whose condition held in the last <see cref="Update"/> but whose duration has not elapsed yet.</summary>
    public IReadOnlyList<Finding> Pending { get; private set; } = [];

    public IReadOnlyList<Finding> Update(IReadOnlyList<Observation> observations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<Finding>();
        var pending = new List<Finding>();
        foreach (var observation in observations)
        {
            var key = observation.Finding.Key;
            seen.Add(key);
            if (!_firstSeen.TryGetValue(key, out var first))
            {
                first = now;
                _firstSeen[key] = first;
            }

            var start = observation.Finding.Since is { } since && since < first ? since : first;
            (now - start >= observation.For ? findings : pending).Add(observation.Finding with { Since = start });
        }

        foreach (var stale in _firstSeen.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _firstSeen.Remove(stale);
        }

        Pending = pending;
        return findings;
    }

    public void Reset()
    {
        _firstSeen.Clear();
        Pending = [];
    }
}

/// <summary>Leader election decision.</summary>
internal enum LeaseAction
{
    /// <summary>No Lease: create it and become leader.</summary>
    Create = 0,

    /// <summary>We are the leader: renew.</summary>
    Renew = 1,

    /// <summary>Expired or without a holder: take over.</summary>
    TakeOver = 2,

    /// <summary>Someone else is leader and it has not expired: follow.</summary>
    Follow = 3,
}

/// <summary>
/// Leader election with a coordination.k8s.io/v1 Lease (same semantics as client-go leaderelection): if we hold it we renew,
/// if renewTime + leaseDurationSeconds has passed we take over (leaseTransitions + 1), otherwise we follow. Writes use
/// optimistic locking via resourceVersion: if two replicas try to take over at the same time, one gets 409 and stays a follower.
/// </summary>
internal static class LeaseDecision
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static (LeaseAction Action, Lease Next) Decide(Lease? current, (string Namespace, string Name, string Identity) me, DateTimeOffset now, int durationSeconds)
    {
        var stamp = Stamp(now);
        if (current?.Spec is null)
        {
            var meta = new ObjectMeta(me.Name, me.Namespace, ResourceVersion: current?.Metadata?.ResourceVersion);
            return (current is null ? LeaseAction.Create : LeaseAction.TakeOver,
                new Lease("coordination.k8s.io/v1", "Lease", meta, new LeaseSpec(me.Identity, durationSeconds, stamp, stamp, 0)));
        }

        var spec = current.Spec;
        if (spec.HolderIdentity == me.Identity)
        {
            return (LeaseAction.Renew, current with { Spec = spec with { RenewTime = stamp, LeaseDurationSeconds = durationSeconds } });
        }

        var renewed = Parse(spec.RenewTime ?? spec.AcquireTime);
        var expired = string.IsNullOrEmpty(spec.HolderIdentity) || renewed is null || renewed.Value.AddSeconds(spec.LeaseDurationSeconds ?? durationSeconds) < now;
        if (!expired)
        {
            return (LeaseAction.Follow, current);
        }

        var next = spec with { HolderIdentity = me.Identity, LeaseDurationSeconds = durationSeconds, AcquireTime = stamp, RenewTime = stamp, LeaseTransitions = (spec.LeaseTransitions ?? 0) + 1 };
        return (LeaseAction.TakeOver, current with { Spec = next });
    }

    public static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
}
