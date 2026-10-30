using Microsoft.Extensions.Time.Testing;
using RintUp.Agent.Tests.Support;

namespace RintUp.Agent.Tests;

/// <summary>Metric collection: delta-based CPU/network, memory, disks, processes.</summary>
public class MetricCollectorTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void First_sample_has_no_cpu_or_network_second_is_computed_from_the_delta()
    {
        var source = FakeSystemSource.WithSamples();
        var collector = new MetricCollector(source, _time);

        var first = collector.Collect([]);
        source.Files["stat"] = FakeSystemSource.Sample("stat-next");
        source.Files["net/dev"] = source.Files["net/dev"].Replace("1000000    2000", "1060000    2000", StringComparison.Ordinal);
        _time.Advance(TimeSpan.FromSeconds(60));
        var second = collector.Collect([]);

        Assert.Null(first.CpuPercent);
        Assert.Null(first.NetRxBytesPerSecond);
        Assert.Equal(75.0, second.CpuPercent);
        Assert.Equal(1000.0, second.NetRxBytesPerSecond);
        Assert.Equal(0.0, second.NetTxBytesPerSecond);
        Assert.Equal(_time.GetUtcNow(), second.Time);
    }

    [Fact]
    public void Memory_swap_load_uptime_and_disks()
    {
        var sample = new MetricCollector(FakeSystemSource.WithSamples(), _time).Collect([]);

        Assert.Equal(8048284L * 1024, sample.MemTotalBytes);
        Assert.Equal((8048284L - 5248284L) * 1024, sample.MemUsedBytes);
        Assert.Equal((2097148L - 1048574L) * 1024, sample.SwapUsedBytes);
        Assert.Equal((0.52, 0.58, 0.59), (sample.Load1!.Value, sample.Load5!.Value, sample.Load15!.Value));
        Assert.Equal(350735L, sample.UptimeSeconds);
        Assert.Equal(["/", "/boot/efi"], sample.Disks.Select(d => d.Mount));
        Assert.Equal((100_000L, 25_000L), (sample.Disks[0].TotalBytes, sample.Disks[0].UsedBytes));
    }

    [Fact]
    public void Watched_processes_match_by_comm_and_by_cmdline_for_long_names()
    {
        var source = FakeSystemSource.WithSamples();
        source.Pids.AddRange([1, 42, 77]);
        source.Files["1/comm"] = "systemd\n";
        source.Files["42/comm"] = "nginx\n";
        source.Files["77/comm"] = "very-long-proce\n";
        source.Files["77/cmdline"] = "/opt/app/very-long-process-name\0--flag";

        var sample = new MetricCollector(source, _time).Collect(["nginx", "redis-server", "very-long-process-name"]);

        Assert.Equal(3, sample.ProcessCount);
        Assert.Equal([("nginx", true), ("redis-server", false), ("very-long-process-name", true)], sample.Processes.Select(p => (p.Name, p.Running)));
    }

    [Fact]
    public void Unreadable_source_returns_null_and_does_not_crash()
    {
        var sample = new MetricCollector(new FakeSystemSource(), _time).Collect(["nginx"]);

        Assert.Null(sample.CpuPercent);
        Assert.Null(sample.MemTotalBytes);
        Assert.Null(sample.ProcessCount);
        Assert.Empty(sample.Disks);
        Assert.False(Assert.Single(sample.Processes).Running);
    }
}
