using System.Security.Cryptography;
using System.Text;

namespace SqlLookout.Service;

/// <summary>PBKDF2-HMAC-SHA256 with a random salt per password. The iteration count is stored so it can be raised later.</summary>
public static class PasswordHasher
{
    public const int CurrentIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, CurrentIterations);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), CurrentIterations);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        var expected = Convert.FromBase64String(hash);
        var actual = Derive(password, Convert.FromBase64String(salt), iterations);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Burns the same time as a real check, so an unknown user name cannot be told apart by response time.</summary>
    public static void Decoy(string password) => Derive(password, new byte[SaltBytes], CurrentIterations);

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
