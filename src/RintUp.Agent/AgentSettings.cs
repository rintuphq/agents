using System.Globalization;
using System.Text.RegularExpressions;

namespace RintUp.Agent;

/// <summary>
/// Agent settings: /etc/rintup-agent.conf (KEY=value lines, # comments) and environment variables
/// (an environment variable overrides the file). The token is never logged and does not appear in <see cref="ToString"/>.
/// RintUp is reached over HTTPS only; plain HTTP only with the local development flag.
/// Kubernetes: a cluster key (<c>rka_</c>) is given instead of a token; in node mode NODE_NAME (downward API)
/// is required, and host filesystems are mounted read-only under HOST_PROC / HOST_ROOT.
/// </summary>
internal sealed partial class AgentSettings
{
    public const string DefaultConfigPath = "/etc/rintup-agent.conf";
    public const string UrlKey = "RINTUP_URL";
    public const string TokenKey = "RINTUP_TOKEN";
    public const string ProcessesKey = "RINTUP_PROCESSES";
    public const string IntervalKey = "RINTUP_INTERVAL_SECONDS";
    public const string AllowHttpKey = "RINTUP_ALLOW_INSECURE_HTTP";
    public const string NodeNameKey = "NODE_NAME";
    public const string HostProcKey = "HOST_PROC";
    public const string HostRootKey = "HOST_ROOT";
    public const string DefaultUrl = "https://rintup.com";
    public const int DefaultIntervalSeconds = 60;
    public const int MaxWatchedProcesses = 50;

    private static readonly string[] s_keys = [UrlKey, TokenKey, ProcessesKey, IntervalKey, AllowHttpKey, NodeNameKey, HostProcKey, HostRootKey];

    private AgentSettings(string baseUrl, string token, IReadOnlyList<string> processes, TimeSpan interval)
    {
        BaseUrl = baseUrl;
        Endpoint = new Uri(baseUrl + "/api/v1/agent/metrics", UriKind.Absolute);
        Token = token;
        Processes = processes;
        Interval = interval;
    }

    /// <summary>RintUp base URL (no trailing /): https://rintup.com.</summary>
    public string BaseUrl { get; }

    /// <summary>Full metrics endpoint: {RINTUP_URL}/api/v1/agent/metrics.</summary>
    public Uri Endpoint { get; }

    public string Token { get; }

    /// <summary>Whether this is a Kubernetes cluster key (<c>rka_</c>).</summary>
    public bool IsClusterKey => Token.StartsWith("rka_", StringComparison.Ordinal);

    /// <summary>Kubernetes node name (only with a cluster key).</summary>
    public string? NodeName { get; private init; }

    /// <summary>Where the host /proc is mounted (default /proc); HostRoot if the root filesystem (for disk metrics) is mounted.</summary>
    public string ProcRoot { get; private init; } = "/proc";

    public string? HostRoot { get; private init; }

    /// <summary>Whether the host is being measured from a container (HOST_PROC was given).</summary>
    public bool HostMode => ProcRoot != "/proc";

    public IReadOnlyList<string> Processes { get; }

    public TimeSpan Interval { get; }

    public override string ToString() =>
        $"endpoint={Endpoint}, processes={Processes.Count}, interval={Interval.TotalSeconds}s{(NodeName is null ? string.Empty : ", node=" + NodeName)}";

    /// <summary>File (if any) + environment. On failure returns a description (contains no values).</summary>
    public static (AgentSettings? Settings, string? Error) Load(string? fileContent, Func<string, string?> environment, bool requireNode = true)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var values = ParseFile(fileContent ?? string.Empty);
        foreach (var key in s_keys)
        {
            if (environment(key) is { Length: > 0 } value)
            {
                values[key] = value;
            }
        }

        var (settings, error) = Build(values);
        return settings is null ? (null, error) : WithKubernetes(settings, values, requireNode);
    }

    internal static Dictionary<string, string> ParseFile(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line.StartsWith('#') || eq <= 0)
            {
                continue;
            }

            values[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
        }

        return values;
    }

    private static (AgentSettings?, string?) Build(Dictionary<string, string> values)
    {
        var token = values.GetValueOrDefault(TokenKey, string.Empty);
        if (!TokenPattern().IsMatch(token))
        {
            return (null, $"{TokenKey} is missing or has an invalid format (must start with srv_, or rka_ for Kubernetes)");
        }

        var allowHttp = string.Equals(values.GetValueOrDefault(AllowHttpKey), "true", StringComparison.OrdinalIgnoreCase);
        var url = values.GetValueOrDefault(UrlKey, DefaultUrl).TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)
            || !(baseUri.Scheme == Uri.UriSchemeHttps || (allowHttp && baseUri.Scheme == Uri.UriSchemeHttp))
            || !string.IsNullOrEmpty(baseUri.UserInfo) || baseUri.Query.Length > 0)
        {
            return (null, $"{UrlKey} is invalid: must be an https:// address");
        }

        var interval = values.TryGetValue(IntervalKey, out var rawInterval) && rawInterval.Length > 0
            ? int.TryParse(rawInterval, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 10 and <= 3600 ? seconds : -1
            : DefaultIntervalSeconds;
        if (interval < 0)
        {
            return (null, $"{IntervalKey} must be 10-3600 seconds");
        }

        var processes = ParseProcesses(values.GetValueOrDefault(ProcessesKey, string.Empty));
        if (processes is null)
        {
            return (null, $"{ProcessesKey} is invalid: comma-separated process names (letters, digits, . _ - + : @), at most 50");
        }

        return (new AgentSettings(url, token, processes, TimeSpan.FromSeconds(interval)), null);
    }

    private static (AgentSettings?, string?) WithKubernetes(AgentSettings settings, Dictionary<string, string> values, bool requireNode)
    {
        var node = values.GetValueOrDefault(NodeNameKey);
        if (settings.IsClusterKey && requireNode && (node is null || !NodeNamePattern().IsMatch(node)))
        {
            return (null, $"{NodeNameKey} is missing or invalid: required for the Kubernetes node agent (downward API spec.nodeName)");
        }

        var proc = values.GetValueOrDefault(HostProcKey);
        var root = values.GetValueOrDefault(HostRootKey);
        if ((proc is not null && !IsAbsoluteDir(proc)) || (root is not null && !IsAbsoluteDir(root)))
        {
            return (null, $"{HostProcKey} / {HostRootKey} must be absolute directories");
        }

        return (new AgentSettings(settings.BaseUrl, settings.Token, settings.Processes, settings.Interval)
        {
            NodeName = settings.IsClusterKey && requireNode ? node : null,
            ProcRoot = proc?.TrimEnd('/') is { Length: > 0 } p ? p : "/proc",
            HostRoot = root?.TrimEnd('/') is { Length: > 0 } r ? r : null,
        }, null);
    }

    private static bool IsAbsoluteDir(string path) => path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal);

    internal static IReadOnlyList<string>? ParseProcesses(string raw)
    {
        var names = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        return names.Count <= MaxWatchedProcesses && names.All(n => ProcessNamePattern().IsMatch(n)) ? names : null;
    }

    [GeneratedRegex("^(srv|rka)_[0-9A-Za-z]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>Kubernetes node name: DNS subdomain, at most 253 (same as the server-side validation).</summary>
    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9._-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeNamePattern();

    /// <summary>Same pattern as the server-side rule validation.</summary>
    [GeneratedRegex("^[A-Za-z0-9._+:@-]{1,64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ProcessNamePattern();
}
