using Microsoft.Data.Sqlite;
using SqlLookout.Service;

namespace SqlLookout.Tests;

public sealed class PasswordResetCommandTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-reset-{Guid.NewGuid():N}.db");
    private SettingsStore _store = null!;
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _store = new SettingsStore(_file, new FakeSecretProtector());
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private async Task<(int Code, string Out, string Err, List<string> Prompts)> RunAsync(string? user, bool askTwice, params string?[] answers)
    {
        var queue = new Queue<string?>(answers);
        var prompts = new List<string>();
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await PasswordResetCommand.RunAsync(_store, user, p => { prompts.Add(p); return queue.Count > 0 ? queue.Dequeue() : null; }, askTwice, output, error);
        return (code, output.ToString(), error.ToString(), prompts);
    }

    [Fact]
    public async Task With_one_account_the_name_is_not_needed_and_the_new_password_works()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        var r = await RunAsync(null, true, "brand-new-password", "brand-new-password");

        Assert.Equal(0, r.Code);
        Assert.Contains("'admin'", r.Out);
        Assert.Equal(2, r.Prompts.Count);
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "brand-new-password", Now));
        Assert.DoesNotContain("brand-new-password", r.Out + r.Err);          // the password is never echoed back
    }

    [Fact]
    public async Task A_typing_mistake_changes_nothing()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        var r = await RunAsync(null, true, "brand-new-password", "brand-new-passwerd");

        Assert.Equal(1, r.Code);
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now));
    }

    [Fact]
    public async Task A_piped_password_is_asked_once_and_too_short_is_refused()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        var piped = await RunAsync(null, false, "brand-new-password");
        Assert.Equal(0, piped.Code);
        Assert.Single(piped.Prompts);

        var shorty = await RunAsync(null, false, "short");
        Assert.Equal(1, shorty.Code);
        Assert.Contains("Nothing was changed", shorty.Err);
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "brand-new-password", Now));

        var none = await RunAsync(null, false);                                   // input ended
        Assert.Equal(1, none.Code);
    }

    [Fact]
    public async Task Several_accounts_need_a_name_and_an_unknown_name_is_reported()
    {
        await _store.CreateAdminAsync("admin", "a-long-enough-password");
        await InsertSecondUserAsync();

        var ambiguous = await RunAsync(null, false, "brand-new-password");
        Assert.Equal(2, ambiguous.Code);
        Assert.Contains("--user", ambiguous.Err);
        Assert.Empty(ambiguous.Prompts);

        var unknown = await RunAsync("nobody", false, "brand-new-password");
        Assert.Equal(1, unknown.Code);
        Assert.Contains("No account named 'nobody'", unknown.Err);

        var named = await RunAsync("OPS", false, "brand-new-password");
        Assert.Equal(0, named.Code);
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("ops", "brand-new-password", Now));
        Assert.Equal(LoginResult.Ok, await _store.VerifyLoginAsync("admin", "a-long-enough-password", Now));
    }

    [Fact]
    public async Task Without_any_account_it_says_so()
    {
        var r = await RunAsync(null, false, "brand-new-password");
        Assert.Equal(1, r.Code);
        Assert.Contains("no account", r.Err, StringComparison.OrdinalIgnoreCase);
    }

    private async Task InsertSecondUserAsync()
    {
        var (hash, salt, iterations) = PasswordHasher.Hash("ops-password-123");
        await using var c = new SqliteConnection($"Data Source={_file}");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Users (UserName, Hash, Salt, Iterations) VALUES ('ops', $h, $s, $i);";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$i", iterations);
        await cmd.ExecuteNonQueryAsync();
    }
}
