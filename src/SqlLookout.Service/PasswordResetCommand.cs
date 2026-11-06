using SqlLookout.Core;

namespace SqlLookout.Service;

/// <summary>
/// <c>sqllookout reset-password</c>: for a forgotten sign-in password. It works on the data folder directly, so it needs
/// access to the machine, which is the same trust the web page's own data already rests on. There is deliberately no
/// "forgot password" link on the web page.
/// </summary>
public static class PasswordResetCommand
{
    /// <param name="readSecret">Asks for a password with the given prompt; null means the input ended.</param>
    /// <param name="askTwice">True when a person types (the password is asked again to catch typos); false when it is piped in.</param>
    public static async Task<int> RunAsync(SettingsStore store, string? userName, Func<string, string?> readSecret, bool askTwice, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var users = await store.GetUserNamesAsync(ct);
        if (users.Count == 0)
        {
            error.WriteLine("There is no account yet. Open the web page once to create the administrator.");
            return 1;
        }

        if (userName is null)
        {
            if (users.Count > 1)
            {
                error.WriteLine("There is more than one account; say which with --user. Accounts: " + string.Join(", ", users));
                return 2;
            }
            userName = users[0];
        }
        else if (!users.Contains(userName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            error.WriteLine($"No account named '{userName}'. Accounts: {string.Join(", ", users)}");
            return 1;
        }

        var password = readSecret($"New password for {userName.Trim()}: ");
        if (password is null)
        {
            error.WriteLine("No password was given.");
            return 1;
        }
        if (askTwice && readSecret("Type it again: ") != password)
        {
            error.WriteLine("The two entries are not the same. Nothing was changed.");
            return 1;
        }

        try
        {
            if (!await store.ResetPasswordAsync(userName, password, ct))
            {
                error.WriteLine("The account disappeared while resetting. Nothing was changed.");
                return 1;
            }
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine(ex.Message + " Nothing was changed.");
            return 1;
        }

        output.WriteLine($"The password of '{userName.Trim()}' was changed. Everyone who was signed in has been signed out, and a lockout is lifted.");
        return 0;
    }
}
