using System.Net;
using System.Security.Authentication;

namespace RintUp.Agent;

/// <summary>HTTP client: TLS 1.2+, certificate validation at defaults (cannot be disabled), no redirects, short timeouts.</summary>
internal static class AgentHttp
{
    public static HttpClient Create(HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("rintup-agent/" + AgentVersion.Current);
        return client;
    }
}

/// <summary>Configuration file: empty if missing (environment variables suffice); warns if group/others can read it.</summary>
internal static class ConfigFile
{
    private const UnixFileMode Loose =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static string? Read(string path, AgentLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & Loose) != 0)
            {
                log.Warning($"{path} is readable by other users: it contains a token, so its mode must be 600 (chmod 600)");
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"{path} could not be read ({ex.GetType().Name})");
            return null;
        }
    }
}
