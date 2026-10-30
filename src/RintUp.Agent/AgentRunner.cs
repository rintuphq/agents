using RintUp.Agent.Wire;

namespace RintUp.Agent;

/// <summary>
/// Loop: measure on each interval, append to the buffer, send the whole buffer in a single request. If the network
/// is down, at most <see cref="MaxBuffered"/> samples are kept in memory (the oldest is dropped) and sent in bulk once connected.
/// Watched processes: those in the configuration + those reported by the server's process rules.
/// </summary>
internal sealed class AgentRunner(MetricCollector collector, MetricSender sender, AgentSettings settings, (TimeProvider Time, AgentLog Log) env)
{
    public const int MaxBuffered = 60;
    public const int MaxRemoteWatch = 50;

    private readonly Queue<MetricSample> _buffer = new();
    private IReadOnlyList<string> _remoteWatch = [];
    private bool _connected;

    public int Buffered => _buffer.Count;

    public IReadOnlyCollection<string> Watched =>
        settings.Processes.Concat(_remoteWatch).Distinct(StringComparer.Ordinal).Take(AgentSettings.MaxWatchedProcesses).ToList();

    public async Task RunAsync(string? hostname, string? os, CancellationToken cancellationToken)
    {
        // In Kubernetes node mode the first send is shifted randomly within the interval: even if DaemonSet pods start at the same
        // time, sends are spread across the interval, so the same nodes are not always rejected at a fixed per-minute limit.
        if (settings.NodeName is not null)
        {
            await Task.Delay(StartJitter(settings.Interval), env.Time, cancellationToken);
        }

        using var timer = new PeriodicTimer(settings.Interval, env.Time);
        do
        {
            await RunOnceAsync(hostname, os, cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    /// <summary>Single round: measure + send. Called directly by tests.</summary>
    public async Task<SendOutcome> RunOnceAsync(string? hostname, string? os, CancellationToken cancellationToken)
    {
        _buffer.Enqueue(collector.Collect(Watched));
        while (_buffer.Count > MaxBuffered)
        {
            _buffer.Dequeue();
        }

        var batch = _buffer.ToList();
        var payload = new MetricsPayload(AgentVersion.Current, hostname, os, env.Time.GetUtcNow(), batch, settings.NodeName);
        var (outcome, watch) = await sender.SendAsync(payload, cancellationToken);
        if (outcome is SendOutcome.Sent or SendOutcome.Drop)
        {
            _buffer.Clear();
        }

        if (outcome == SendOutcome.Sent)
        {
            _remoteWatch = (watch ?? []).Where(n => AgentSettings.ProcessNamePattern().IsMatch(n)).Take(MaxRemoteWatch).ToList();
            if (!_connected)
            {
                env.Log.Info($"Connected to RintUp; {batch.Count} samples sent");
            }
        }

        _connected = outcome == SendOutcome.Sent;
        return outcome;
    }

    /// <summary>Random delay between 0 and the interval (RandomNumberGenerator; at most 5 min).</summary>
    public static TimeSpan StartJitter(TimeSpan interval) =>
        TimeSpan.FromMilliseconds(System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, (int)Math.Clamp(interval.TotalMilliseconds, 1, 300_000)));
}

internal static class AgentVersion
{
    public const string Current = "1.1.0";
}
