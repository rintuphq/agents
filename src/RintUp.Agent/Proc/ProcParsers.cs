using System.Globalization;
using System.Text;

namespace RintUp.Agent.Proc;

/// <summary>CPU counters (first line of /proc/stat, jiffies). Idle = idle + iowait.</summary>
internal readonly record struct CpuTimes(ulong Total, ulong Idle);

/// <summary>/proc/meminfo summary (bytes).</summary>
internal readonly record struct MemInfo(long Total, long Available, long SwapTotal, long SwapFree);

/// <summary>Total network counters (excluding loopback, bytes).</summary>
internal readonly record struct NetCounters(ulong Rx, ulong Tx);

/// <summary>Mount point (/proc/mounts).</summary>
internal readonly record struct MountEntry(string Device, string Path, string FsType);

/// <summary>
/// Pure parsers for /proc files (they do not read files; tests call them with sample content).
/// Return null on malformed/missing content: the agent does not send that value and does not crash.
/// </summary>
internal static class ProcParsers
{
    /// <summary>Real disk filesystems; virtual ones (proc, tmpfs, overlay, squashfs...) are skipped.</summary>
    private static readonly HashSet<string> s_diskFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "jfs", "reiserfs", "vfat", "exfat", "ntfs", "ntfs3",
        "fuseblk", "nfs", "nfs4", "cifs", "smb3", "ceph", "glusterfs", "bcachefs",
    };

    // On a Kubernetes node, pod volumes and container layers (mounts of the same device) are not counted as disks.
    private static readonly string[] s_virtualPrefixes =
    [
        "/proc", "/sys", "/dev", "/run/user", "/snap", "/var/lib/kubelet/pods", "/var/lib/kubelet/plugins", "/var/lib/containerd",
        "/run/containerd", "/var/lib/docker/overlay2", "/run/k3s",
    ];

    public static CpuTimes? ParseCpu(string stat)
    {
        var line = FirstLine(stat);
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).ToArray();
        var values = new ulong[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        // user nice system idle iowait irq softirq steal (guest is already counted in user).
        return values.Length < 4 ? null : new CpuTimes(values.Aggregate(0UL, (a, b) => a + b), values[3] + (values.Length > 4 ? values[4] : 0));
    }

    /// <summary>CPU usage (%) between two samples; null if the counter went backwards or did not change.</summary>
    public static double? CpuPercent(CpuTimes previous, CpuTimes current)
    {
        if (current.Total <= previous.Total || current.Idle < previous.Idle)
        {
            return null;
        }

        var total = (double)(current.Total - previous.Total);
        var busy = total - (current.Idle - previous.Idle);
        return Math.Round(Math.Clamp(100.0 * busy / total, 0, 100), 2);
    }

    public static MemInfo? ParseMemInfo(string meminfo)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in meminfo.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var parts = colon > 0 ? line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
            {
                values[line[..colon]] = kb * 1024;
            }
        }

        if (!values.TryGetValue("MemTotal", out var total) || total <= 0)
        {
            return null;
        }

        // Older kernels have no MemAvailable: approximated as free + buffers + cached.
        var available = values.TryGetValue("MemAvailable", out var a)
            ? a
            : values.GetValueOrDefault("MemFree") + values.GetValueOrDefault("Buffers") + values.GetValueOrDefault("Cached");
        return new MemInfo(total, Math.Min(available, total), values.GetValueOrDefault("SwapTotal"), values.GetValueOrDefault("SwapFree"));
    }

    public static (double One, double Five, double Fifteen)? ParseLoadAvg(string loadavg)
    {
        var parts = loadavg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && Number(parts[0]) is { } one && Number(parts[1]) is { } five && Number(parts[2]) is { } fifteen
            ? (one, five, fifteen)
            : null;
    }

    public static long? ParseUptime(string uptime)
    {
        var first = uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && Number(first) is { } seconds ? (long)seconds : null;
    }

    public static NetCounters? ParseNetDev(string netdev)
    {
        ulong rx = 0, tx = 0;
        var found = false;
        foreach (var line in netdev.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || line[..colon].Trim() is "lo" or "" || line[..colon].Contains('|', StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 9 && ulong.TryParse(fields[0], CultureInfo.InvariantCulture, out var r) && ulong.TryParse(fields[8], CultureInfo.InvariantCulture, out var t))
            {
                (rx, tx, found) = (rx + r, tx + t, true);
            }
        }

        return found ? new NetCounters(rx, tx) : null;
    }

    /// <summary>Bytes/s; null if the counter was reset (the interface restarted).</summary>
    public static (double Rx, double Tx)? NetRate(NetCounters previous, NetCounters current, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero && current.Rx >= previous.Rx && current.Tx >= previous.Tx
            ? (Math.Round((current.Rx - previous.Rx) / elapsed.TotalSeconds, 1), Math.Round((current.Tx - previous.Tx) / elapsed.TotalSeconds, 1))
            : null;

    /// <summary>Real disk mount points; a second mount of the same device (bind mount) is skipped.</summary>
    public static IReadOnlyList<MountEntry> ParseMounts(string mounts, int max)
    {
        var result = new List<MountEntry>();
        var devices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in mounts.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !s_diskFileSystems.Contains(fields[2]))
            {
                continue;
            }

            var path = Unescape(fields[1]);
            var isVirtual = s_virtualPrefixes.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
            if (!isVirtual && path.StartsWith('/') && devices.Add(fields[0]) && result.Count < max)
            {
                result.Add(new MountEntry(fields[0], path, fields[2]));
            }
        }

        return result;
    }

    /// <summary>Program name from /proc/[pid]/cmdline (the last segment of argv[0]).</summary>
    public static string? ProgramName(string cmdline)
    {
        var argv0 = cmdline.Split('\0', 2)[0].Trim();
        return argv0.Length == 0 ? null : argv0[(argv0.LastIndexOf('/') + 1)..];
    }

    /// <summary>/etc/os-release PRETTY_NAME.</summary>
    public static string? ParseOsRelease(string osRelease) =>
        osRelease.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            .Select(l => l["PRETTY_NAME=".Length..].Trim('"', '\''))
            .FirstOrDefault(v => v.Length > 0);

    private static string? FirstLine(string text)
    {
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        return end < 0 ? text.TrimEnd() : text[..end].TrimEnd();
    }

    private static double? Number(string value) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary>/proc/mounts octal escapes (\040 space, \011 tab, \012, \134).</summary>
    private static string Unescape(string path)
    {
        if (!path.Contains('\\', StringComparison.Ordinal))
        {
            return path;
        }

        var builder = new StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\' && i + 3 < path.Length && IsOctal(path.AsSpan(i + 1, 3)))
            {
                builder.Append((char)Convert.ToInt32(path.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(path[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(ReadOnlySpan<char> digits) => digits.Length == 3 && digits[0] is >= '0' and <= '7' && digits[1] is >= '0' and <= '7' && digits[2] is >= '0' and <= '7';
}
