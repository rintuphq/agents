namespace RintUp.Agent;

/// <summary>
/// Agent log: one line to stderr, with a systemd/journald priority prefix (&lt;3&gt; error, &lt;4&gt; warning,
/// &lt;6&gt; info). No non-BCL logging package (AOT, single file). Tokens, headers and bodies are NEVER written
/// to messages; for exceptions only the type name.
/// </summary>
internal sealed class AgentLog(TextWriter writer)
{
    private readonly Lock _gate = new();

    public void Info(string message) => Write(6, message);

    public void Warning(string message) => Write(4, message);

    public void Error(string message) => Write(3, message);

    private void Write(int priority, string message)
    {
        lock (_gate)
        {
            writer.WriteLine($"<{priority}>{message}");
            writer.Flush();
        }
    }
}
