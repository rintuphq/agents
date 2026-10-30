using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using RintUp.Agent.Kube;
using static RintUp.Agent.Tests.Kube.KubeSamples;

namespace RintUp.Agent.Tests.Kube;

/// <summary>
/// In-memory fake Kubernetes API: recorded list responses, /version and a single Lease with optimistic locking (409 on
/// resourceVersion mismatch, 404 if missing). Requests (path + Authorization) are recorded. No real network.
/// </summary>
internal sealed class FakeKubeApi : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private Lease? _lease;
    private int _version = 100;

    public ConcurrentQueue<(HttpMethod Method, string PathAndQuery, string? Authorization)> Requests { get; } = new();

    public bool FailPods { get; set; }

    public Lease? CurrentLease
    {
        get
        {
            lock (_gate)
            {
                return _lease;
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Enqueue((request.Method, path, request.Headers.Authorization?.ToString()));
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path.StartsWith("/apis/coordination.k8s.io/", StringComparison.Ordinal))
        {
            return LeaseCall(request.Method, body);
        }

        var route = request.RequestUri.AbsolutePath;
        return route switch
        {
            "/version" => Json("""{"major":"1","minor":"31","gitVersion":"v1.31.2"}"""),
            "/api/v1/nodes" => Json(Text("nodes.json")),
            "/api/v1/pods" => FailPods ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json(Text("pods.json")),
            "/apis/apps/v1/deployments" => Json(Workload("deployments")),
            "/apis/apps/v1/statefulsets" => Json(Workload("statefulsets")),
            "/apis/apps/v1/daemonsets" => Json(Workload("daemonsets")),
            "/apis/apps/v1/replicasets" => Json(Workload("replicasets")),
            "/apis/batch/v1/jobs" => Json(Workload("jobs")),
            "/api/v1/events" => Json(Workload("events")),
            "/api/v1/persistentvolumeclaims" => Json(Workload("claims")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage LeaseCall(HttpMethod method, string? body)
    {
        lock (_gate)
        {
            if (method == HttpMethod.Get)
            {
                return _lease is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(Serialize(_lease));
            }

            var incoming = JsonSerializer.Deserialize(body!, KubeJsonContext.Default.Lease)!;
            if ((method == HttpMethod.Post && _lease is not null)
                || (method == HttpMethod.Put && (_lease is null || incoming.Metadata?.ResourceVersion != _lease.Metadata?.ResourceVersion)))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            _version++;
            _lease = incoming with { Metadata = incoming.Metadata! with { ResourceVersion = _version.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            return Json(Serialize(_lease));
        }
    }

    private static string Serialize(Lease lease) => JsonSerializer.Serialize(lease, KubeJsonContext.Default.Lease);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
