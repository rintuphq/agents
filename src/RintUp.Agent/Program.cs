using System.Runtime.InteropServices;
using RintUp.Agent;
using RintUp.Agent.Kube;
using RintUp.Agent.Proc;

// rintup-agent: sends server metrics to RintUp. Root is not required.
// Configuration: /etc/rintup-agent.conf (mode 600) or environment variables (RINTUP_URL, RINTUP_TOKEN,
// RINTUP_PROCESSES). Usage: rintup-agent [--config <path>] | rintup-agent cluster | --version
// Kubernetes (Helm chart: https://github.com/rintuphq/helm-charts, charts/rintup-agent):
//   DaemonSet  : rintup-agent            (RINTUP_TOKEN=rka_..., NODE_NAME, HOST_PROC=/host/proc, HOST_ROOT=/host/root)
//   Deployment : rintup-agent cluster    (RINTUP_TOKEN=rka_..., POD_NAME, POD_NAMESPACE; read-only API access via a service account)
var log = new AgentLog(Console.Error);
if (args is ["--version"])
{
    await Console.Out.WriteLineAsync("rintup-agent " + AgentVersion.Current);
    return 0;
}

var clusterMode = args is ["cluster", ..];
var configPath = args is ["--config", var path] ? path : AgentSettings.DefaultConfigPath;
var (settings, error) = AgentSettings.Load(ConfigFile.Read(configPath, log), Environment.GetEnvironmentVariable, requireNode: !clusterMode);
if (settings is null || (clusterMode && !settings.IsClusterKey))
{
    log.Error("Invalid configuration: " + (error ?? "cluster mode requires a Kubernetes cluster key (rka_)"));
    return 2;
}

using var stopping = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stopping.Cancel();
});
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stopping.Cancel();
});

using var http = AgentHttp.Create();
try
{
    return clusterMode ? await RunClusterAsync(settings, http, log, stopping.Token) : await RunNodeAsync(settings, http, log, stopping.Token);
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    log.Info("rintup-agent stopped");
    return 0;
}

static async Task<int> RunNodeAsync(AgentSettings settings, HttpClient http, AgentLog log, CancellationToken cancellationToken)
{
    var source = new LinuxSystemSource(settings.ProcRoot, settings.HostRoot, settings.NodeName);
    var runner = new AgentRunner(new MetricCollector(source, TimeProvider.System), new MetricSender(http, settings, log), settings, (TimeProvider.System, log));
    log.Info($"rintup-agent {AgentVersion.Current} started ({settings})");
    var osRelease = settings.HostMode ? (settings.HostRoot is { } root ? source.Read(root + "/etc/os-release") : null) : source.Read("/etc/os-release");
    await runner.RunAsync(source.Hostname(), ProcParsers.ParseOsRelease(osRelease ?? string.Empty), cancellationToken);
    return 0;
}

static async Task<int> RunClusterAsync(AgentSettings settings, HttpClient http, AgentLog log, CancellationToken cancellationToken)
{
    var (config, error) = KubeConfig.FromEnvironment(Environment.GetEnvironmentVariable, ReadFile);
    if (config is null)
    {
        log.Error("Cluster mode could not start: " + error);
        return 2;
    }

    using var kubeHttp = KubeClient.CreateHttp(config);
    var kube = new KubeClient(kubeHttp, config, TimeProvider.System);
    var leaseName = Environment.GetEnvironmentVariable("RINTUP_LEASE_NAME") is { Length: > 0 and <= 63 } name ? name : LeaderElector.LeaseName;
    var elector = new LeaderElector(kube, (config.Namespace, leaseName, config.Identity), TimeProvider.System);
    var runner = new ClusterRunner((kube, elector, new ClusterSender(http, settings)), (TimeProvider.System, log));
    log.Info($"rintup-agent {AgentVersion.Current} started in cluster mode (endpoint={settings.BaseUrl}, namespace={config.Namespace})");
    await runner.RunAsync(cancellationToken);
    return 0;
}

static string? ReadFile(string path)
{
    try
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}
