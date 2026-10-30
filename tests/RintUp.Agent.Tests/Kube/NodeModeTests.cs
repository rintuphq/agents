using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RintUp.Agent.Proc;
using RintUp.Agent.Tests.Support;

namespace RintUp.Agent.Tests.Kube;

/// <summary>Node agent (DaemonSet) mode: an rka_ key requires NODE_NAME, the host /proc is read from the PID 1 namespace, the node name is sent.</summary>
public sealed class NodeModeTests : IDisposable
{
    private const string Key = "rka_0123456789abcdefghijABCDEFGHIJ0123456789abc";
    private readonly string _root = Directory.CreateTempSubdirectory("rintup-hostproc").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Theory]
    [InlineData(null)]
    [InlineData("bad name")]
    [InlineData("-node")]
    public void Cluster_key_requires_a_valid_NODE_NAME(string? node)
    {
        var (settings, error) = AgentSettings.Load(null, Env(("RINTUP_TOKEN", Key), ("NODE_NAME", node!)));

        Assert.Null(settings);
        Assert.Contains("NODE_NAME", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Node_mode_settings_and_cluster_mode_does_not_require_node_name()
    {
        var (node, _) = AgentSettings.Load(null, Env(("RINTUP_TOKEN", Key), ("NODE_NAME", "ip-10-0-1-23.eu-central-1.compute.internal"), ("HOST_PROC", "/host/proc/"), ("HOST_ROOT", "/host/root")));
        var (cluster, clusterError) = AgentSettings.Load(null, Env(("RINTUP_TOKEN", Key)), requireNode: false);
        var (_, relative) = AgentSettings.Load(null, Env(("RINTUP_TOKEN", Key), ("NODE_NAME", "n"), ("HOST_PROC", "host/../proc")));

        Assert.Equal(("ip-10-0-1-23.eu-central-1.compute.internal", "/host/proc", "/host/root", true, true), (node!.NodeName, node.ProcRoot, node.HostRoot, node.HostMode, node.IsClusterKey));
        Assert.DoesNotContain(Key, node.ToString(), StringComparison.Ordinal);
        Assert.Null(clusterError);
        Assert.Null(cluster!.NodeName);
        Assert.Contains("HOST_PROC", relative, StringComparison.Ordinal);
    }

    [Fact]
    public void In_host_mode_mounts_and_network_are_read_from_the_PID_1_namespace()
    {
        Directory.CreateDirectory(Path.Combine(_root, "1", "net"));
        File.WriteAllText(Path.Combine(_root, "1", "mounts"), "host-mounts");
        File.WriteAllText(Path.Combine(_root, "1", "net", "dev"), "host-net");
        File.WriteAllText(Path.Combine(_root, "mounts"), "container-mounts");
        File.WriteAllText(Path.Combine(_root, "loadavg"), "0.1 0.2 0.3 1/2 3");
        var source = new LinuxSystemSource(_root, null, "node-a");

        Assert.Equal(("host-mounts", "host-net", "0.1 0.2 0.3 1/2 3"), (source.Read("mounts"), source.Read("net/dev"), source.Read("loadavg")));
        Assert.Equal("node-a", source.Hostname());
        Assert.Null(source.DiskSpace("/"));
    }

    [Fact]
    public void Kubelet_and_container_mounts_are_not_counted_as_disks()
    {
        const string mounts = "/dev/sda1 / ext4 rw 0 0\n/dev/sdb /var/lib/kubelet/pods/abc/volumes/kubernetes.io~csi/pv/mount ext4 rw 0 0\n"
            + "/dev/sdc /var/lib/containerd/io.containerd.snapshotter ext4 rw 0 0\n/dev/sdd /data xfs rw 0 0\n";

        Assert.Equal(["/", "/data"], ProcParsers.ParseMounts(mounts, 64).Select(m => m.Path));
    }

    [Fact]
    public async Task Send_includes_node_name_and_cluster_key()
    {
        var (settings, _) = AgentSettings.Load(null, Env(("RINTUP_TOKEN", Key), ("NODE_NAME", "node-a"), ("RINTUP_URL", "https://rintup.example")));
        using var handler = new ScriptedHandler();
        using var http = AgentHttp.Create(handler);
        using var logs = new StringWriter();
        var time = new FakeTimeProvider(KubeSamples.Now);
        var runner = new AgentRunner(new MetricCollector(FakeSystemSource.WithSamples(), time), new MetricSender(http, settings!, new AgentLog(logs)), settings!, (time, new AgentLog(logs)));

        await runner.RunOnceAsync("node-a", "Ubuntu 24.04", TestContext.Current.CancellationToken);

        var (authorization, body, _) = Assert.Single(handler.Requests);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Bearer " + Key, authorization);
        Assert.Equal("node-a", json.RootElement.GetProperty("nodeName").GetString());
    }

    /// <summary>In node mode the first send is shifted randomly within the interval (sends are spread over the minute).</summary>
    [Fact]
    public void First_send_jitter_is_within_the_interval()
    {
        var jitters = Enumerable.Range(0, 50).Select(_ => AgentRunner.StartJitter(TimeSpan.FromSeconds(60))).ToList();

        Assert.All(jitters, j => Assert.InRange(j, TimeSpan.Zero, TimeSpan.FromSeconds(60)));
        Assert.True(jitters.Distinct().Count() > 1);
        Assert.InRange(AgentRunner.StartJitter(TimeSpan.FromHours(1)), TimeSpan.Zero, TimeSpan.FromMinutes(5));
    }
}
