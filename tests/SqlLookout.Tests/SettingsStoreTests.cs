using System.Text.Json;
using Microsoft.Data.Sqlite;
using SqlLookout.Alerts;
using SqlLookout.Service;

namespace SqlLookout.Tests;

/// <summary>The settings store tests; <see cref="SqlServerSettingsStoreTests"/> runs every one of them against SQL Server.</summary>
public class SettingsStoreTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-set-{Guid.NewGuid():N}.db");
    protected SettingsStore _store = null!;
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>False in the SQL Server run: the tests that build an old SQLite file by hand do not apply there.</summary>
    protected virtual bool IsSqlite => true;

    public virtual async Task InitializeAsync()
    {
        _store = new SettingsStore(_file, new FakeSecretProtector());
        await _store.InitializeAsync();
    }

    public virtual async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    /// <summary>Everything stored in the user, instance and settings tables as one text, to prove secrets are not in it.</summary>
    protected virtual string RawDump()
    {
        using var c = new SqliteConnection($"Data Source={_file}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(group_concat(UserName || '|' || Hash || '|' || Salt, ';'), '') FROM Users;
            """;
        var users = (string)cmd.ExecuteScalar()!;
        cmd.CommandText = "SELECT COALESCE(group_concat(Name || '|' || Server || '|' || COALESCE(UserName,'') || '|' || COALESCE(PasswordProtected,''), ';'), '') FROM Instances;";
        var instances = (string)cmd.ExecuteScalar()!;
        cmd.CommandText = "SELECT COALESCE(group_concat(Key || '|' || Value, ';'), '') FROM Settings;";
        var settings = (string)cmd.ExecuteScalar()!;
        return users + "\n" + instances + "\n" + settings;
    }

    // ---- accounts ----
    [Fact]
    public async Task First_admin_can_be_created_once_and_logs_in()
    {
        Assert.False(await _store.HasAdminAsync());
        await _store.CreateAdminAsync("admin", "a-long-enough-password");

        Assert.True(await _store.HasAdminAsync());
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now));
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("ADMIN", "a-long-enough-password", Now));   // user names are case-insensitive
        Assert.Equal(LoginResult.Invalid, await _store.VerifyLoginAsync("admin", "wrong-password-here", Now));
        Assert.Equal(LoginResult.Invalid, await _store.VerifyLoginAsync("nobody", "a-long-enough-password", Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateAdminAsync("second", "another-long-password"));
    }

    [Fact]
    public async Task Short_passwords_and_names_are_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.CreateAdminAsync("admin", "short"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.CreateAdminAsync("ab", "a-long-enough-password"));
        Assert.False(await _store.HasAdminAsync());
    }

    [Fact]
    public async Task Password_is_stored_hashed_not_as_typed()
    {
        await _store.CreateAdminAsync("admin", "my-secret-passphrase");
        Assert.DoesNotContain("my-secret-passphrase", RawDump());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_five_minutes_even_for_the_right_password()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        for (var i = 0; i < 5; i++)
            Assert.Equal(LoginResult.Invalid, await _store.VerifyLoginAsync("admin", "wrong-password-" + i, Now));

        Assert.Equal(LoginResult.Locked, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now.AddMinutes(1)));
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now.AddMinutes(6)));
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        for (var i = 0; i < 4; i++) await _store.VerifyLoginAsync("admin", "wrong-password-" + i, Now);
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now));
        for (var i = 0; i < 4; i++) await _store.VerifyLoginAsync("admin", "wrong-password-" + i, Now);

        // 4 + 4 failures, but a success in between: still not locked.
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now));
    }

    [Fact]
    public async Task Change_password_needs_the_current_one()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ChangePasswordAsync("admin", "not-the-password", "brand-new-password", Now));
        await _store.ChangePasswordAsync("admin", "a-long-enough-password", "brand-new-password", Now);

        Assert.Equal(LoginResult.Invalid, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now));
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "brand-new-password", Now));
    }

    [Fact]
    public async Task Reset_password_sets_a_new_one_without_the_old_lifts_the_lockout_and_signs_everyone_out()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        var stampBefore = await _store.RotateSessionStampAsync("admin");
        for (var i = 0; i < 5; i++) await _store.VerifyLoginAsync("admin", "wrong-password-" + i, Now);
        Assert.Equal(LoginResult.Locked, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now.AddMinutes(1)));

        Assert.True(await _store.ResetPasswordAsync("ADMIN", "forgot-and-reset-1"));

        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "forgot-and-reset-1", Now.AddMinutes(1)));
        Assert.Equal(LoginResult.Invalid, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now.AddMinutes(1)));
        Assert.NotEqual(stampBefore, await _store.GetSessionStampAsync("admin"));
    }

    [Fact]
    public async Task Reset_password_refuses_a_short_password_and_reports_an_unknown_account()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ResetPasswordAsync("admin", "short"));
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now));

        Assert.False(await _store.ResetPasswordAsync("nobody", "another-long-password"));
        Assert.Equal(["admin"], await _store.GetUserNamesAsync());
    }

    // ---- instances ----
    private static InstanceRecord Sql(string name = "prod", string user = "mon") =>
        new(name, "PROD01", AuthModes.Sql, user, EncryptModes.Trust, true, false);

    [Fact]
    public async Task Ignore_list_is_saved_cleaned_and_reaches_the_built_config()
    {
        await _store.SaveInstanceAsync(Sql() with { IgnoredDatabases = " Test* ,\nSqlLookoutAge_*; test* " }, "pw", isNew: true);

        Assert.Equal("Test*\nSqlLookoutAge_*", (await _store.GetInstanceAsync("prod"))!.IgnoredDatabases);   // cleaned, repeats dropped
        var config = await _store.BuildConfigAsync();
        Assert.Equal(["Test*", "SqlLookoutAge_*"], config.Instances.Single().IgnoredDatabases);
        Assert.Equal(["Test*", "SqlLookoutAge_*"], config.IgnoredFor("PROD"));       // name match ignores case
        Assert.Empty(config.IgnoredFor("other"));

        await _store.SaveInstanceAsync(Sql() with { IgnoredDatabases = "" }, "", isNew: false);   // clearing it works
        Assert.Empty((await _store.BuildConfigAsync()).Instances.Single().IgnoredDatabases);
    }

    [Fact]
    public async Task Bad_ignore_list_is_refused_and_nothing_is_saved()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql() with { IgnoredDatabases = new string('x', 200) }, "pw", isNew: true));
        Assert.Null(await _store.GetInstanceAsync("prod"));
    }

    [Fact]
    public async Task A_settings_file_made_before_the_ignore_list_existed_is_upgraded_and_keeps_its_servers()
    {
        if (!IsSqlite) return;
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
        using (var c = new SqliteConnection($"Data Source={_file}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Instances (Name TEXT PRIMARY KEY COLLATE NOCASE, Server TEXT NOT NULL, Auth TEXT NOT NULL, UserName TEXT NULL,
                    PasswordProtected TEXT NULL, Encrypt TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1);
                INSERT INTO Instances (Name, Server, Auth, Encrypt, Enabled) VALUES ('old', 'OLD01', 'windows', 'trust', 1);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        _store = new SettingsStore(_file, new FakeSecretProtector());
        await _store.InitializeAsync();

        var old = (await _store.GetInstancesAsync()).Single();
        Assert.Equal("OLD01", old.Server);
        Assert.Equal("", old.IgnoredDatabases);
    }

    [Fact]
    public async Task Sql_password_is_encrypted_at_rest_and_comes_back_in_the_connection_string()
    {
        await _store.SaveInstanceAsync(Sql(), "Pa55;word=x", isNew: true);

        Assert.DoesNotContain("Pa55", RawDump());
        var cs = await _store.GetConnectionStringAsync("prod");
        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        Assert.Equal("PROD01", parsed.DataSource);
        Assert.Equal("mon", parsed.UserID);
        Assert.Equal("Pa55;word=x", parsed.Password);   // special characters survive: the string is built, not concatenated
        Assert.True((await _store.GetInstanceAsync("prod"))!.HasPassword);
    }

    [Fact]
    public async Task Blank_password_on_edit_keeps_the_saved_one_and_a_new_one_replaces_it()
    {
        await _store.SaveInstanceAsync(Sql(), "first-password", isNew: true);
        await _store.SaveInstanceAsync(Sql() with { Server = "PROD02" }, "", isNew: false);

        var kept = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder((await _store.GetConnectionStringAsync("prod"))!);
        Assert.Equal("PROD02", kept.DataSource);
        Assert.Equal("first-password", kept.Password);

        await _store.SaveInstanceAsync(Sql(), "second-password", isNew: false);
        Assert.Equal("second-password", new Microsoft.Data.SqlClient.SqlConnectionStringBuilder((await _store.GetConnectionStringAsync("prod"))!).Password);
    }

    [Fact]
    public async Task Windows_authentication_has_no_password_and_uses_integrated_security()
    {
        await _store.SaveInstanceAsync(new("w", "PROD01", AuthModes.Windows, null, EncryptModes.Trust, true, false), null, isNew: true);
        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder((await _store.GetConnectionStringAsync("w"))!);
        Assert.True(parsed.IntegratedSecurity);
        Assert.Equal("", parsed.UserID);
        Assert.False((await _store.GetInstanceAsync("w"))!.HasPassword);
    }

    [Theory]
    [InlineData(EncryptModes.Off, false, false)]
    [InlineData(EncryptModes.Trust, true, true)]
    [InlineData(EncryptModes.Verify, true, false)]
    public void Encryption_option_maps_to_the_connection_string(string mode, bool encrypt, bool trust)
    {
        var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            new InstanceRecord("x", "s", AuthModes.Windows, null, mode, true, false).BuildConnectionString(null));
        Assert.Equal(encrypt, cs.Encrypt == Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory);
        Assert.Equal(trust, cs.TrustServerCertificate);
    }

    [Fact]
    public async Task Invalid_instance_input_is_refused_with_a_readable_message()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql(name: ""), "p", true));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql() with { Server = " " }, "p", true));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql(user: ""), "p", true));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql(), "", true));                       // new SQL login without a password
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql() with { Auth = "kerberos" }, "p", true));

        await _store.SaveInstanceAsync(Sql(), "p", true);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql(name: "PROD"), "p", true));          // names are case-insensitive
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql(name: "other"), "p", false));        // edit of something that does not exist
    }

    [Fact]
    public async Task Delete_removes_the_instance()
    {
        await _store.SaveInstanceAsync(Sql(), "p", true);
        await _store.DeleteInstanceAsync("prod");
        Assert.Empty(await _store.GetInstancesAsync());
        Assert.Null(await _store.GetConnectionStringAsync("prod"));
    }

    // ---- alert settings and the built configuration ----
    private const string TeamsUrl = "https://prod-1.westus.logic.azure.com/workflows/abc?sig=TEAMS-SECRET";

    [Fact]
    public async Task The_teams_address_is_stored_encrypted_kept_on_blank_and_needed_when_teams_is_on()
    {
        var settings = new AlertSettings { Teams = new TeamsSettings() };
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAlertSettingsAsync(settings, null, false));                       // on, but no address
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAlertSettingsAsync(settings, null, false, default, "http://insecure.example/hook"));
        Assert.False(await _store.HasTeamsUrlAsync());

        await _store.SaveAlertSettingsAsync(settings, null, false, default, TeamsUrl);
        Assert.DoesNotContain("TEAMS-SECRET", RawDump());
        Assert.DoesNotContain("TEAMS-SECRET", JsonSerializer.Serialize(await _store.GetAlertSettingsAsync()));
        Assert.True(await _store.HasTeamsUrlAsync());
        Assert.Equal(TeamsUrl, (await _store.BuildConfigAsync()).Alerts.Teams!.ResolveUrl());

        await _store.SaveAlertSettingsAsync(settings, null, false);                                                                          // blank keeps it
        Assert.Equal(TeamsUrl, (await _store.BuildConfigAsync()).Alerts.Teams!.ResolveUrl());

        await _store.SaveAlertSettingsAsync(new AlertSettings(), null, false);                                                               // off keeps the address for later
        Assert.Null((await _store.BuildConfigAsync()).Alerts.Teams);
        Assert.True(await _store.HasTeamsUrlAsync());

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAlertSettingsAsync(settings, null, false, default, null, clearTeamsUrl: true));
        await _store.SaveAlertSettingsAsync(new AlertSettings(), null, false, default, null, clearTeamsUrl: true);
        Assert.False(await _store.HasTeamsUrlAsync());
    }

    [Fact]
    public async Task A_bad_teams_address_saves_nothing_at_all()
    {
        var before = await _store.GetAlertSettingsAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveSettingsAsync(
            new AlertSettings { Teams = new TeamsSettings(), RenotifyHours = 9 }, new GlobalSettings(), null, false, default, "ftp://nope"));
        Assert.Equal(before.RenotifyHours, (await _store.GetAlertSettingsAsync()).RenotifyHours);
    }

    [Fact]
    public async Task The_sender_serves_the_channels_that_are_set_up()
    {
        var smtp = new EmailSettings { Host = "h", From = "a@b.c", To = ["x@y.z"] };
        Assert.IsType<NullAlertSender>(AlertSenders.For(new AlertSettings(), () => new NullAlertSender()));
        Assert.IsType<SmtpAlertSender>(AlertSenders.For(new AlertSettings { Email = smtp }, () => new NullAlertSender()));
        Assert.IsType<TeamsAlertSender>(AlertSenders.For(new AlertSettings { Teams = new TeamsSettings { Url = TeamsUrl } }, () => new NullAlertSender()));
        var both = AlertSenders.For(new AlertSettings { Email = smtp, Teams = new TeamsSettings { Url = TeamsUrl } }, () => new NullAlertSender());
        Assert.IsType<CompositeAlertSender>(both);
        Assert.True(both.Delivers);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Alert_settings_round_trip_and_the_smtp_password_is_encrypted_and_kept_on_blank()
    {
        var settings = new AlertSettings
        {
            Email = new EmailSettings { Host = "smtp.local", Port = 587, UseSsl = true, User = "u", From = "a@b.c", To = ["x@y.z"] },
            RenotifyHours = 6,
            QuietStart = "22:00",
            QuietEnd = "07:00",
        };
        settings.Thresholds.DiskWarnFreeGb = 77;
        await _store.SaveAlertSettingsAsync(settings, "smtp-secret", clearSmtpPassword: false);

        Assert.DoesNotContain("smtp-secret", RawDump());
        var loaded = await _store.GetAlertSettingsAsync();
        Assert.Equal("smtp.local", loaded.Email!.Host);
        Assert.Equal(77, loaded.Thresholds.DiskWarnFreeGb);
        Assert.Equal(6, loaded.RenotifyHours);
        Assert.Null(loaded.Email.Password);                          // never part of the stored JSON
        Assert.True(await _store.HasSmtpPasswordAsync());

        await _store.SaveAlertSettingsAsync(loaded, null, clearSmtpPassword: false);       // blank keeps it
        Assert.Equal("smtp-secret", (await _store.BuildConfigAsync()).Alerts.Email!.ResolvePassword());

        await _store.SaveAlertSettingsAsync(loaded, null, clearSmtpPassword: true);
        Assert.False(await _store.HasSmtpPasswordAsync());
    }

    [Fact]
    public async Task Invalid_alert_settings_are_not_saved()
    {
        var bad = new AlertSettings { QuietStart = "22:00" };      // end missing
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAlertSettingsAsync(bad, null, false));
        Assert.Null((await _store.GetAlertSettingsAsync()).QuietStart);
    }

    [Fact]
    public async Task Built_config_carries_instances_with_decrypted_connection_strings_and_global_settings()
    {
        await _store.SaveInstanceAsync(Sql(), "p4ss", true);
        await _store.SaveInstanceAsync(Sql("off") with { Enabled = false }, "p4ss", true);
        await _store.SaveGlobalAsync(new GlobalSettings { RetentionDays = 30, DisabledCollectors = ["agent_jobs"] });

        var config = await _store.BuildConfigAsync();

        Assert.Equal(30, config.RetentionDays);
        Assert.Contains("agent_jobs", config.DisabledCollectors);
        Assert.Equal(2, config.Instances.Count);
        Assert.True(config.Instances.Single(i => i.Name == "prod").Enabled);
        Assert.False(config.Instances.Single(i => i.Name == "off").Enabled);
        Assert.Contains("p4ss", config.Instances.Single(i => i.Name == "prod").ResolveConnectionString());
    }

    [Fact]
    public async Task Retention_must_be_sensible()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveGlobalAsync(new GlobalSettings { RetentionDays = 0 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveGlobalAsync(new GlobalSettings { RetentionDays = 99999 }));
    }

    [Fact]
    public async Task Live_config_picks_up_changes_only_after_refresh()
    {
        var live = new LiveConfig(_store);
        await live.RefreshAsync();
        Assert.Empty(live.Current.Instances);

        await _store.SaveInstanceAsync(Sql(), "p", true);
        Assert.Empty(live.Current.Instances);
        await live.RefreshAsync();
        Assert.Single(live.Current.Instances);
    }
}
