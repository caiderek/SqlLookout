using System.Security.Cryptography;
using System.Text;

namespace SqlLookout.Service;

/// <summary>Encrypts the few secrets the web UI stores (SQL passwords, the SMTP password, the certificate password).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

/// <summary>
/// Windows DPAPI with machine scope: the settings file is useless if it is copied to another machine, and the
/// service account and an interactive run read the same secrets. It does not stop an administrator of this
/// machine, which is the same limit every DPAPI-based tool has.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "SqlLookout.v1"u8.ToArray();

    public string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine));

    public string Unprotect(string protectedText) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.LocalMachine));
}
