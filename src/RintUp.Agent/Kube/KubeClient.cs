using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization.Metadata;

namespace RintUp.Agent.Kube;

/// <summary>Kubernetes API error: status code only (body/headers are not logged).</summary>
internal sealed class KubeApiException(int status) : Exception($"Kubernetes API HTTP {status.ToString(CultureInfo.InvariantCulture)}")
{
    public int Status { get; } = status;
}

/// <summary>
/// In-cluster configuration (service account): API address from KUBERNETES_SERVICE_HOST/PORT, token and CA from the mounted
/// secret volume, namespace from POD_NAMESPACE (else the service account file), identity from POD_NAME (else HOSTNAME).
/// </summary>
internal sealed record KubeConfig(Uri ApiBase, string TokenPath, string CaPath, string Namespace, string Identity)
{
    public const string ServiceAccountDir = "/var/run/secrets/kubernetes.io/serviceaccount";

    public static (KubeConfig? Config, string? Error) FromEnvironment(Func<string, string?> environment, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readFile);
        var host = environment("KUBERNETES_SERVICE_HOST");
        var port = environment("KUBERNETES_SERVICE_PORT") ?? "443";
        if (string.IsNullOrEmpty(host) || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
        {
            return (null, "Not running in Kubernetes: KUBERNETES_SERVICE_HOST/PORT missing (the cluster agent only runs inside a pod)");
        }

        var ns = environment("POD_NAMESPACE") ?? readFile(ServiceAccountDir + "/namespace")?.Trim();
        var identity = environment("POD_NAME") ?? environment("HOSTNAME");
        if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(identity))
        {
            return (null, "POD_NAMESPACE and POD_NAME (downward API) are required");
        }

        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var api = new Uri($"https://{authority}:{p.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute);
        return (new KubeConfig(api, ServiceAccountDir + "/token", ServiceAccountDir + "/ca.crt", ns, identity), null);
    }
}

/// <summary>
/// Read-only Kubernetes API client (only list + Lease get/create/update + /version). TLS: cluster CA only
/// (CustomRootTrust; validation is never disabled), no proxy (in-cluster address), redirects are not followed. The token file
/// is re-read every minute (the projected service account token rotates). Paged list: limit=500 + continue.
/// </summary>
internal sealed class KubeClient(HttpClient http, KubeConfig config, TimeProvider time)
{
    public const int PageSize = 500;
    private static readonly TimeSpan s_tokenTtl = TimeSpan.FromMinutes(1);

    private (string Token, DateTimeOffset ReadAt)? _token;

    public static HttpClient CreateHttp(KubeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(config.CaPath);
        policy.CustomTrustStore.AddRange(roots);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateChainPolicy = policy },
        };
        return new HttpClient(handler) { BaseAddress = config.ApiBase, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<IReadOnlyList<T>> ListAsync<T>(string path, JsonTypeInfo<KubeList<T>> type, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? next = null;
        do
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var url = $"{path}{separator}limit={PageSize.ToString(CultureInfo.InvariantCulture)}{(next is null ? string.Empty : "&continue=" + Uri.EscapeDataString(next))}";
            using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
            var page = await response.Content.ReadFromJsonAsync(type, cancellationToken) ?? throw new KubeApiException((int)response.StatusCode);
            items.AddRange(page.Items ?? []);
            next = page.Metadata?.Continue is { Length: > 0 } c ? c : null;
        }
        while (next is not null);

        return items;
    }

    /// <summary>The Lease; null if it does not exist.</summary>
    public Task<Lease?> GetLeaseAsync(string ns, string name, CancellationToken cancellationToken) =>
        LeaseCallAsync(HttpMethod.Get, LeasePath(ns, name), null, cancellationToken);

    /// <summary>Creates it; null if someone else created it first (409).</summary>
    public Task<Lease?> CreateLeaseAsync(Lease lease, CancellationToken cancellationToken) =>
        LeaseCallAsync(HttpMethod.Post, $"/apis/coordination.k8s.io/v1/namespaces/{Uri.EscapeDataString(lease.Metadata!.Namespace!)}/leases", lease, cancellationToken);

    /// <summary>Updates it with resourceVersion; null if someone else got in between (409).</summary>
    public Task<Lease?> UpdateLeaseAsync(Lease lease, CancellationToken cancellationToken) =>
        LeaseCallAsync(HttpMethod.Put, LeasePath(lease.Metadata!.Namespace!, lease.Metadata.Name!), lease, cancellationToken);

    public async Task<string?> VersionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/version", null, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(KubeJsonContext.Default.VersionInfo, cancellationToken))?.GitVersion;
    }

    private static string LeasePath(string ns, string name) =>
        $"/apis/coordination.k8s.io/v1/namespaces/{Uri.EscapeDataString(ns)}/leases/{Uri.EscapeDataString(name)}";

    private async Task<Lease?> LeaseCallAsync(HttpMethod method, string path, Lease? body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(method, path, body, cancellationToken);
            return await response.Content.ReadFromJsonAsync(KubeJsonContext.Default.Lease, cancellationToken);
        }
        catch (KubeApiException ex) when (ex.Status is 404 or 409)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Lease? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken));
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, KubeJsonContext.Default.Lease);
        }

        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new KubeApiException(status);
        }

        return response;
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_token is not { } cached || now - cached.ReadAt > s_tokenTtl)
        {
            _token = ((await File.ReadAllTextAsync(config.TokenPath, cancellationToken)).Trim(), now);
        }

        return _token.Value.Token;
    }
}
