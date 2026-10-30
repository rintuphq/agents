using System.Text.Json;
using RintUp.Agent.Kube;

namespace RintUp.Agent.Tests.Kube;

/// <summary>Recorded Kubernetes API responses (Samples/kube/*.json; real API format, fictional cluster). Reference time 12:00Z.</summary>
internal static class KubeSamples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "kube", name));

    public static ClusterSnapshot Snapshot()
    {
        var json = KubeJsonContext.Default;
        using var workloads = JsonDocument.Parse(Text("workloads.json"));
        var root = workloads.RootElement;
        return new ClusterSnapshot(
            Items(JsonSerializer.Deserialize(Text("nodes.json"), json.KubeListNode)),
            Items(JsonSerializer.Deserialize(Text("pods.json"), json.KubeListPod)),
            Items(root.GetProperty("deployments").Deserialize(json.KubeListDeployment)),
            Items(root.GetProperty("statefulsets").Deserialize(json.KubeListStatefulSet)),
            Items(root.GetProperty("daemonsets").Deserialize(json.KubeListDaemonSet)),
            Items(root.GetProperty("replicasets").Deserialize(json.KubeListReplicaSet)),
            Items(root.GetProperty("jobs").Deserialize(json.KubeListJob)),
            Items(root.GetProperty("events").Deserialize(json.KubeListKubeEvent)),
            Items(root.GetProperty("claims").Deserialize(json.KubeListPersistentVolumeClaim)));
    }

    public static string Workload(string name)
    {
        using var workloads = JsonDocument.Parse(Text("workloads.json"));
        return workloads.RootElement.GetProperty(name).GetRawText();
    }

    private static IReadOnlyList<T> Items<T>(KubeList<T>? list) => list?.Items ?? throw new InvalidOperationException("sample could not be read");
}
