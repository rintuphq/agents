using System.Text.Json.Serialization;

namespace RintUp.Agent.Wire;

// Wire contract: the SAME field names (camelCase) as the RintUp API's agent contract.
// It is a copy because the agent does not reference other RintUp projects; fields may be added, existing ones are never changed.
// Byte fields are bytes, rates are bytes/s, percentages are 0-100. An unreadable value is null.

internal sealed record MetricsPayload(
    string AgentVersion,
    string? Hostname,
    string? Os,
    DateTimeOffset SentAt,
    IReadOnlyList<MetricSample> Samples,
    string? NodeName = null);

internal sealed record MetricSample(
    DateTimeOffset Time,
    double? CpuPercent,
    double? Load1,
    double? Load5,
    double? Load15,
    long? MemTotalBytes,
    long? MemUsedBytes,
    long? MemAvailableBytes,
    long? SwapTotalBytes,
    long? SwapUsedBytes,
    IReadOnlyList<DiskSample> Disks,
    double? NetRxBytesPerSecond,
    double? NetTxBytesPerSecond,
    long? UptimeSeconds,
    int? ProcessCount,
    IReadOnlyList<ProcessSample> Processes);

internal sealed record DiskSample(string Mount, long TotalBytes, long UsedBytes);

internal sealed record ProcessSample(string Name, bool Running);

/// <summary>Server response: number of accepted samples and processes to watch (from process rules).</summary>
internal sealed record MetricsAck(int Accepted, IReadOnlyList<string>? Watch);

/// <summary>Source-generated JSON (AOT: no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MetricsPayload))]
[JsonSerializable(typeof(MetricsAck))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
