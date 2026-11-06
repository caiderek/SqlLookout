using SqlLookout.Service;

namespace SqlLookout.Tests;

public class ConfigTests
{
    private static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqllookout-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Minimal_config_loads_with_defaults()
    {
        var path = Write("""{ "instances": [ { "name": "a", "connectionString": "Server=x;Integrated Security=true" } ] }""");
        var c = SqlLookoutConfig.Load(path);
        Assert.Equal(90, c.RetentionDays);
        Assert.Equal(26, c.Alerts.Thresholds.FullBackupWarnHours);
        Assert.Equal(TimeSpan.FromHours(8), c.Alerts.Summary());
    }

    [Fact]
    public void Teams_can_be_set_up_in_the_file_by_environment_variable_or_file_and_never_by_the_address_itself()
    {
        const string instance = """ "instances": [ { "name": "a", "connectionString": "Server=x;Integrated Security=true" } ] """;
        var env = "SQLLOOKOUT_TEST_TEAMS_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(env, "https://example.com/hook?sig=abc");
        var c = SqlLookoutConfig.Load(Write("{ \"alerts\": { \"teams\": { \"urlEnv\": \"" + env + "\" } }," + instance + "}"));
        Assert.Equal("https://example.com/hook?sig=abc", c.Alerts.Teams!.ResolveUrl());
        Assert.IsType<SqlLookout.Alerts.TeamsAlertSender>(AlertSenders.For(c.Alerts, () => new SqlLookout.Alerts.NullAlertSender()));

        // A "url" property in the file is ignored on purpose: the address is a secret and has no place in a settings file.
        var inline = SqlLookoutConfig.Load(Write("{ \"alerts\": { \"teams\": { \"url\": \"https://example.com/x\" } }," + instance + "}"));
        Assert.Null(inline.Alerts.Teams!.ResolveUrl());
    }

    [Fact]
    public void Look_is_classic_unless_the_file_says_terminal_and_anything_else_is_refused()
    {
        const string instance = """ "instances": [ { "name": "a", "connectionString": "Server=x;Integrated Security=true" } ] """;
        Assert.Equal("classic", SqlLookoutConfig.Load(Write("{" + instance + "}")).Look);
        Assert.Equal("terminal", SqlLookoutConfig.Load(Write("{ \"look\": \"terminal\"," + instance + "}")).Look);
        var ex = Assert.Throws<InvalidDataException>(() => SqlLookoutConfig.Load(Write("{ \"look\": \"neon\"," + instance + "}")));
        Assert.Contains("look must be one of", ex.Message);
    }

    [Theory]
    [InlineData("Server=x;User Id=sa;Password=secret")]
    [InlineData("Server=x;User Id=sa;PASSWORD=secret")]
    [InlineData("Server=x;User Id=sa;Pwd=secret")]
    public void Password_in_the_config_file_is_refused(string connectionString)
    {
        var path = Write($$"""{ "instances": [ { "name": "a", "connectionString": "{{connectionString}}" } ] }""");
        var ex = Assert.Throws<InvalidDataException>(() => SqlLookoutConfig.Load(path));
        Assert.Contains("connectionStringFile", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public void Connection_string_file_may_hold_a_password()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"sqllookout-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "Server=x;User Id=u;Password=p\r\n");
        var path = Write($$"""{ "instances": [ { "name": "a", "connectionStringFile": {{System.Text.Json.JsonSerializer.Serialize(secret)}} } ] }""");
        var c = SqlLookoutConfig.Load(path);
        Assert.Equal("Server=x;User Id=u;Password=p", c.Instances[0].ResolveConnectionString());
    }

    [Fact]
    public void Missing_or_duplicate_instances_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => SqlLookoutConfig.Load(Write("""{ "instances": [] }""")));
        Assert.Throws<InvalidDataException>(() => SqlLookoutConfig.Load(Write(
            """{ "instances": [ { "name": "a", "connectionString": "S=1" }, { "name": "A", "connectionString": "S=2" } ] }""")));
        Assert.Throws<InvalidDataException>(() => SqlLookoutConfig.Load(Write(
            """{ "instances": [ { "name": "a" } ] }""")));
    }

    [Fact]
    public void Quiet_hours_must_come_as_a_pair_and_email_needs_recipients()
    {
        Assert.Throws<InvalidDataException>(() => SqlLookoutConfig.Load(Write(
            """{ "instances": [ { "name": "a", "connectionString": "S=1" } ], "alerts": { "quietStart": "22:00" } }""")));
        Assert.Throws<InvalidDataException>(() => SqlLookoutConfig.Load(Write(
            """{ "instances": [ { "name": "a", "connectionString": "S=1" } ], "alerts": { "email": { "host": "smtp", "from": "a@b.c", "to": [] } } }""")));
    }

    [Fact]
    public void Thresholds_can_be_overridden_and_comments_are_allowed()
    {
        var path = Write("""
            {
              // trailing commas and comments are fine
              "instances": [ { "name": "a", "connectionString": "S=1", }, ],
              "alerts": { "thresholds": { "diskWarnFreeGb": 50 }, "disabledRules": ["dbcc"] }
            }
            """);
        var c = SqlLookoutConfig.Load(path);
        Assert.Equal(50, c.Alerts.Thresholds.DiskWarnFreeGb);
        Assert.Contains("dbcc", c.Alerts.DisabledRules);
    }
}
