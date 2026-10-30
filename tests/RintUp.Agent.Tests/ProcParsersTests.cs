using RintUp.Agent.Proc;
using static RintUp.Agent.Tests.Support.FakeSystemSource;

namespace RintUp.Agent.Tests;

/// <summary>/proc parsers with realistic sample files (Samples/).</summary>
public class ProcParsersTests
{
    [Fact]
    public void Cpu_counters_and_percentage_between_two_samples()
    {
        var first = ProcParsers.ParseCpu(Sample("stat"))!.Value;
        var next = ProcParsers.ParseCpu(Sample("stat-next"))!.Value;

        Assert.Equal(new CpuTimes(9390, 3722), first);
        Assert.Equal(75.0, ProcParsers.CpuPercent(first, next));
        Assert.Null(ProcParsers.CpuPercent(next, first));
        Assert.Null(ProcParsers.CpuPercent(first, first));
        Assert.Null(ProcParsers.ParseCpu("intr 1 2 3"));
        Assert.Null(ProcParsers.ParseCpu("cpu  x y z w"));
    }

    [Fact]
    public void Meminfo_available_and_old_kernel_approximation()
    {
        var info = ProcParsers.ParseMemInfo(Sample("meminfo"))!.Value;
        var old = ProcParsers.ParseMemInfo(Sample("meminfo-old"))!.Value;

        Assert.Equal(new MemInfo(8048284L * 1024, 5248284L * 1024, 2097148L * 1024, 1048574L * 1024), info);
        Assert.Equal(400 * 1024, old.Available);
        Assert.Null(ProcParsers.ParseMemInfo("garbage"));
    }

    [Fact]
    public void Loadavg_uptime_and_os_release()
    {
        Assert.Equal((0.52, 0.58, 0.59), ProcParsers.ParseLoadAvg(Sample("loadavg")));
        Assert.Equal(350735L, ProcParsers.ParseUptime(Sample("uptime")));
        Assert.Equal("Ubuntu 24.04.1 LTS", ProcParsers.ParseOsRelease(Sample("os-release")));
        Assert.Null(ProcParsers.ParseLoadAvg("1.0"));
        Assert.Null(ProcParsers.ParseUptime(string.Empty));
    }

    [Fact]
    public void Net_dev_total_excluding_loopback_and_rate()
    {
        var counters = ProcParsers.ParseNetDev(Sample("net-dev"))!.Value;

        Assert.Equal(new NetCounters(1_002_000, 501_000), counters);
        Assert.Equal((100.0, 50.0), ProcParsers.NetRate(new NetCounters(996_000, 498_000), counters, TimeSpan.FromSeconds(60)));
        Assert.Null(ProcParsers.NetRate(counters, new NetCounters(0, 0), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Mounts_lists_real_disks_with_unique_devices_and_unescaped_paths()
    {
        var mounts = ProcParsers.ParseMounts(Sample("mounts"), 64);

        Assert.Equal(["/", "/boot/efi", "/mnt/my data", "/var/lib/docker"], mounts.Select(m => m.Path));
        Assert.Equal(2, ProcParsers.ParseMounts(Sample("mounts"), 2).Count);
    }

    [Theory]
    [InlineData("/usr/sbin/nginx\0-g\0daemon off;", "nginx")]
    [InlineData("postgres: checkpointer", "postgres: checkpointer")]
    [InlineData("", null)]
    public void Cmdline_program_name(string cmdline, string? expected) => Assert.Equal(expected, ProcParsers.ProgramName(cmdline));
}
