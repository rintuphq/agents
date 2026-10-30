using System.Text.Json.Serialization;

namespace RintUp.Agent.Kube;

// Cluster agent wire contract: POST {RINTUP_URL}/api/v1/agent/kubernetes (Bearer rka_...).
// The SAME field names (camelCase) as the RintUp API's cluster contract. The report is a full snapshot: an open
// finding missing from the list is resolved on the server (Pending: observations whose duration condition has not
// been met yet are not resolved). That is why it is only sent when all lists could be read.

internal sealed record ClusterReportPayload(
    string AgentVersion,
    string? KubernetesVersion,
    IReadOnlyList<NodeState> Nodes,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Finding> Pending);

internal sealed record NodeState(string Name, bool Ready);

/// <summary>Finding. Deduplication key (the server adds the cluster name): namespace/kind/name/reason.</summary>
internal sealed record Finding(string? Namespace, string Kind, string Name, string Reason, string Severity, string Message, DateTimeOffset? Since)
{
    [JsonIgnore]
    public string Key => $"{Namespace ?? "-"}/{Kind}/{Name}/{Reason}";
}

internal sealed record ClusterReportAck(int Findings, int Opened, int Resolved);

/// <summary>Severity names (same as the RintUp incident center).</summary>
internal static class Severity
{
    public const string Critical = "critical";
    public const string Error = "error";
    public const string Warning = "warning";

    public static int Rank(string severity) => severity switch
    {
        Critical => 3,
        Error => 2,
        Warning => 1,
        _ => 0,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClusterReportPayload))]
[JsonSerializable(typeof(ClusterReportAck))]
internal sealed partial class ClusterJsonContext : JsonSerializerContext;
