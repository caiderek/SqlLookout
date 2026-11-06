using System.Text;
using SqlLookout.Service;

namespace SqlLookout.Tests;

/// <summary>Reversible and obviously not plaintext, so a test can tell "encrypted before storing" from "stored as typed".</summary>
public sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(plaintext.Reverse().ToArray())));

    public string Unprotect(string protectedText) =>
        new string(Encoding.UTF8.GetString(Convert.FromBase64String(protectedText["enc:".Length..])).Reverse().ToArray());
}
