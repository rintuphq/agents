namespace RintUp.Agent.Tests;

/// <summary>Configuration: file + environment, HTTPS only, token format, token is not logged.</summary>
public class AgentSettingsTests
{
    private const string Token = "srv_0123456789abcdefghijABCDEFGHIJ0123456789abc";

    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void File_is_read_and_environment_variable_overrides_it()
    {
        var file = $"""
            # RintUp agent
            RINTUP_URL="https://rintup.example/"
            RINTUP_TOKEN={Token}
            RINTUP_PROCESSES=nginx, postgres ,nginx
            """;

        var (settings, error) = AgentSettings.Load(file, Env(("RINTUP_URL", "https://eu.rintup.example")));

        Assert.Null(error);
        Assert.Equal("https://eu.rintup.example/api/v1/agent/metrics", settings!.Endpoint.ToString());
        Assert.Equal(["nginx", "postgres"], settings.Processes);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.Interval);
        Assert.DoesNotContain(Token, settings.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://rintup.example", null)]
    [InlineData("ftp://rintup.example", null)]
    [InlineData("https://user:pass@rintup.example", null)]
    [InlineData("https://rintup.example?x=1", null)]
    [InlineData("not a url", null)]
    [InlineData("https://rintup.example", "srv_short")]
    [InlineData("https://rintup.example", "rup_live_0123456789abcdefghijABCDEFGHIJ0123456")]
    public void Invalid_address_or_token_is_rejected_and_message_contains_no_value(string url, string? token)
    {
        var (settings, error) = AgentSettings.Load(null, Env(("RINTUP_URL", url), ("RINTUP_TOKEN", token ?? Token)));

        Assert.Null(settings);
        Assert.NotNull(error);
        Assert.DoesNotContain(token ?? Token, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_http_only_with_explicit_flag()
    {
        var (settings, _) = AgentSettings.Load(null, Env(("RINTUP_URL", "http://localhost:5080"), ("RINTUP_TOKEN", Token), ("RINTUP_ALLOW_INSECURE_HTTP", "true")));

        Assert.Equal("http://localhost:5080/api/v1/agent/metrics", settings!.Endpoint.ToString());
    }

    [Theory]
    [InlineData("RINTUP_PROCESSES", "rm -rf")]
    [InlineData("RINTUP_INTERVAL_SECONDS", "5")]
    [InlineData("RINTUP_INTERVAL_SECONDS", "abc")]
    public void Invalid_process_list_and_interval(string key, string value)
    {
        var (settings, error) = AgentSettings.Load(null, Env(("RINTUP_TOKEN", Token), (key, value)));

        Assert.Null(settings);
        Assert.Contains(key, error, StringComparison.Ordinal);
    }
}
