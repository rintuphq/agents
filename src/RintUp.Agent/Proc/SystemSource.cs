using System.Globalization;

namespace RintUp.Agent.Proc;

/// <summary>System reads (a fake source backed by sample files in tests). An unreadable value is null; never throws.</summary>
internal interface ISystemSource
{
    /// <summary>A file under /proc (relative path: "stat", "123/comm") or an absolute path.</summary>
    string? Read(string path);

    IReadOnlyList<int> ProcessIds();

    /// <summary>Size of the mount point, with the same meaning as df (used + free available to non-root, used).</summary>
    (long Total, long Used)? DiskSpace(string mount);

    string? Hostname();
}

/// <summary>
/// Real Linux source. Root is not required: /proc and statvfs are world-readable.
/// Host mode (Kubernetes DaemonSet): the host /proc is mounted read-only at <c>procRoot</c> (e.g. /host/proc) and the root
/// filesystem at <c>hostRoot</c> (e.g. /host/root). Mount points and network counters are read from the namespace of the host
/// init process (PID 1), not the container's own network/mount namespace; disk size uses hostRoot + mount point.
/// </summary>
internal sealed class LinuxSystemSource(string procRoot = "/proc", string? hostRoot = null, string? hostname = null) : ISystemSource
{
    private bool HostMode => procRoot != "/proc";

    public string? Read(string path)
    {
        try
        {
            var relative = HostMode && path is "mounts" or "net/dev" ? "1/" + path : path;
            return File.ReadAllText(Path.IsPathRooted(relative) ? relative : Path.Combine(procRoot, relative));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<int> ProcessIds()
    {
        try
        {
            return Directory.EnumerateDirectories(procRoot)
                .Select(Path.GetFileName)
                .Select(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : -1)
                .Where(pid => pid > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public (long Total, long Used)? DiskSpace(string mount)
    {
        try
        {
            if (HostMode && hostRoot is null)
            {
                return null;
            }

            var drive = new DriveInfo(hostRoot is null ? mount : hostRoot + mount);
            var used = drive.TotalSize - drive.TotalFreeSpace;
            var total = used + drive.AvailableFreeSpace;
            return total > 0 && used >= 0 ? (total, used) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public string? Hostname() => hostname ?? (Read("sys/kernel/hostname")?.Trim() is { Length: > 0 } name ? name : Environment.MachineName);
}
