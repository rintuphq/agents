using System.Text.Json.Serialization;

namespace RintUp.Agent.Kube;

// ONLY the fields of Kubernetes API objects needed for finding detection and leader election (read-only monitoring).
// The official client library uses reflection, which is problematic under AOT; the types live here and use JSON source
// generation (KubeJsonContext). Unknown fields are ignored. Naming follows Kubernetes (camelCase).

internal sealed record KubeList<T>(ListMeta? Metadata, IReadOnlyList<T>? Items);

internal sealed record ListMeta(string? Continue, string? ResourceVersion);

internal sealed record ObjectMeta(
    string? Name,
    string? Namespace,
    DateTimeOffset? CreationTimestamp = null,
    DateTimeOffset? DeletionTimestamp = null,
    IReadOnlyList<OwnerReference>? OwnerReferences = null,
    string? ResourceVersion = null);

internal sealed record OwnerReference(string? Kind, string? Name, bool? Controller);

internal sealed record Condition(string? Type, string? Status, string? Reason, string? Message, DateTimeOffset? LastTransitionTime);

internal sealed record Taint(string? Key, string? Effect);

internal sealed record NodeSpec(IReadOnlyList<Taint>? Taints, bool? Unschedulable);

internal sealed record NodeStatus(IReadOnlyList<Condition>? Conditions);

internal sealed record Node(ObjectMeta? Metadata, NodeSpec? Spec, NodeStatus? Status);

internal sealed record ContainerStateWaiting(string? Reason, string? Message);

internal sealed record ContainerStateTerminated(string? Reason, int? ExitCode, DateTimeOffset? FinishedAt);

internal sealed record ContainerState(ContainerStateWaiting? Waiting, ContainerStateTerminated? Terminated);

internal sealed record ContainerStatus(string? Name, int RestartCount, ContainerState? State, ContainerState? LastState);

internal sealed record PodStatus(string? Phase, string? Reason, IReadOnlyList<ContainerStatus>? ContainerStatuses, IReadOnlyList<ContainerStatus>? InitContainerStatuses);

internal sealed record Pod(ObjectMeta? Metadata, PodStatus? Status);

internal sealed record ReplicaSpec(int? Replicas);

internal sealed record WorkloadStatus(int? Replicas, int? ReadyReplicas, int? AvailableReplicas);

internal sealed record Deployment(ObjectMeta? Metadata, ReplicaSpec? Spec, WorkloadStatus? Status);

internal sealed record StatefulSet(ObjectMeta? Metadata, ReplicaSpec? Spec, WorkloadStatus? Status);

internal sealed record DaemonSetStatus(int? DesiredNumberScheduled, int? NumberReady, int? NumberAvailable);

internal sealed record DaemonSet(ObjectMeta? Metadata, DaemonSetStatus? Status);

internal sealed record ReplicaSet(ObjectMeta? Metadata);

internal sealed record JobStatus(IReadOnlyList<Condition>? Conditions);

internal sealed record Job(ObjectMeta? Metadata, JobStatus? Status);

internal sealed record PvcStatus(string? Phase);

internal sealed record PersistentVolumeClaim(ObjectMeta? Metadata, PvcStatus? Status);

internal sealed record ObjectReference(string? Kind, string? Name, string? Namespace);

internal sealed record EventSeries(int? Count, DateTimeOffset? LastObservedTime);

internal sealed record KubeEvent(
    ObjectMeta? Metadata,
    ObjectReference? InvolvedObject,
    string? Reason,
    string? Message,
    string? Type,
    int? Count,
    DateTimeOffset? LastTimestamp,
    DateTimeOffset? EventTime,
    EventSeries? Series);

/// <summary>coordination.k8s.io/v1 Lease. Times are MicroTime strings (6 fractional digits, "Z"): format in <see cref="LeaseDecision"/>.</summary>
internal sealed record LeaseSpec(string? HolderIdentity, int? LeaseDurationSeconds, string? AcquireTime, string? RenewTime, int? LeaseTransitions);

internal sealed record Lease(string? ApiVersion, string? Kind, ObjectMeta? Metadata, LeaseSpec? Spec);

internal sealed record VersionInfo(string? GitVersion);

/// <summary>Source-generated JSON (AOT: no reflection). Null fields are not written (Lease create/update body).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KubeList<Node>))]
[JsonSerializable(typeof(KubeList<Pod>))]
[JsonSerializable(typeof(KubeList<Deployment>))]
[JsonSerializable(typeof(KubeList<StatefulSet>))]
[JsonSerializable(typeof(KubeList<DaemonSet>))]
[JsonSerializable(typeof(KubeList<ReplicaSet>))]
[JsonSerializable(typeof(KubeList<Job>))]
[JsonSerializable(typeof(KubeList<KubeEvent>))]
[JsonSerializable(typeof(KubeList<PersistentVolumeClaim>))]
[JsonSerializable(typeof(Lease))]
[JsonSerializable(typeof(VersionInfo))]
internal sealed partial class KubeJsonContext : JsonSerializerContext;
