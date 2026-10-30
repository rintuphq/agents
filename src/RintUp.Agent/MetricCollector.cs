using System.Globalization;
using RintUp.Agent.Proc;
using RintUp.Agent.Wire;

namespace RintUp.Agent;

/// <summary>
/// Takes one sample. CPU and network rate are the difference between two samples: null on the first sample. Process names
/// are matched via /proc/[pid]/comm (names longer than 15 characters via the program name in cmdline).
/// </summary>
internal sealed class MetricCollector(ISystemSource source, TimeProvider time)
{
    public const int MaxDisks = 64;
    private const int CommLength = 15;

    private CpuTimes? _cpu;
    private (NetCounters Counters, DateTimeOffset At)? _net;

    public MetricSample Collect(IReadOnlyCollection<string> watched)
    {
        ArgumentNullException.ThrowIfNull(watched);
        var now = time.GetUtcNow();
        var load = ProcParsers.ParseLoadAvg(source.Read("loadavg") ?? string.Empty);
        var memory = ProcParsers.ParseMemInfo(source.Read("meminfo") ?? string.Empty);
        var network = NetworkRate(now);
        var pids = source.ProcessIds();
        return new MetricSample(
            now,
            CpuPercent(),
            load?.One,
            load?.Five,
            load?.Fifteen,
            memory?.Total,
            memory is { } m ? m.Total - m.Available : null,
            memory?.Available,
            memory?.SwapTotal,
            memory is { } s ? Math.Max(0, s.SwapTotal - s.SwapFree) : null,
            Disks(),
            network?.Rx,
            network?.Tx,
            ProcParsers.ParseUptime(source.Read("uptime") ?? string.Empty),
            pids.Count > 0 ? pids.Count : null,
            Processes(watched, pids));
    }

    private double? CpuPercent()
    {
        var current = ProcParsers.ParseCpu(source.Read("stat") ?? string.Empty);
        var previous = _cpu;
        _cpu = current;
        return previous is { } p && current is { } c ? ProcParsers.CpuPercent(p, c) : null;
    }

    private (double Rx, double Tx)? NetworkRate(DateTimeOffset now)
    {
        var current = ProcParsers.ParseNetDev(source.Read("net/dev") ?? string.Empty);
        var previous = _net;
        _net = current is { } c ? (c, now) : null;
        return previous is { } p && current is { } cur ? ProcParsers.NetRate(p.Counters, cur, now - p.At) : null;
    }

    private List<DiskSample> Disks() =>
        ProcParsers.ParseMounts(source.Read("mounts") ?? string.Empty, MaxDisks)
            .Select(m => source.DiskSpace(m.Path) is { } space ? new DiskSample(m.Path, space.Total, space.Used) : null)
            .OfType<DiskSample>()
            .ToList();

    private List<ProcessSample> Processes(IReadOnlyCollection<string> watched, IReadOnlyList<int> pids)
    {
        if (watched.Count == 0)
        {
            return [];
        }

        var running = new HashSet<string>(StringComparer.Ordinal);
        var needsCmdline = watched.Any(n => n.Length > CommLength);
        foreach (var pid in pids)
        {
            var id = pid.ToString(CultureInfo.InvariantCulture);
            if (source.Read(id + "/comm")?.Trim() is { Length: > 0 } comm)
            {
                running.Add(comm);
            }

            if (needsCmdline && ProcParsers.ProgramName(source.Read(id + "/cmdline") ?? string.Empty) is { } program)
            {
                running.Add(program);
            }
        }

        return watched.Select(n => new ProcessSample(n, running.Contains(n))).ToList();
    }
}
