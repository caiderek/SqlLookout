using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SqlLookout.Service;

namespace SqlLookout.Web;

/// <summary>
/// The dashboard only speaks HTTPS. Without a certificate of your own, a self-signed one is created on first start
/// and kept (its password is DPAPI-protected). Browsers warn about a self-signed certificate; pass --cert to use your own.
/// </summary>
public static class CertificateProvider
{
    private static readonly TimeSpan RenewWithin = TimeSpan.FromDays(30);

    public static X509Certificate2 LoadOrCreate(string dataDirectory, ISecretProtector protector, string? pfxPath, string? passwordFile, out string source)
    {
        if (pfxPath is not null)
        {
            if (!File.Exists(pfxPath)) throw new FileNotFoundException($"The certificate file was not found: {pfxPath}");
            var password = passwordFile is null ? null : File.ReadAllText(passwordFile).Trim();
            source = $"certificate file {pfxPath}";
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);
            }
            catch (CryptographicException ex)
            {
                throw new IOException($"The certificate file {pfxPath} could not be read: wrong password (--cert-password-file) or not a .pfx file.", ex);
            }
        }

        Directory.CreateDirectory(dataDirectory);
        var pfx = Path.Combine(dataDirectory, "cert.pfx");
        var key = Path.Combine(dataDirectory, "cert.key");

        if (File.Exists(pfx) && File.Exists(key))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(pfx, protector.Unprotect(File.ReadAllText(key)));
                if (existing.NotAfter > DateTime.Now + RenewWithin)
                {
                    source = $"self-signed certificate ({pfx})";
                    return existing;
                }
                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Unreadable (for example copied from another machine): create a fresh one below.
            }
        }

        var created = CreateSelfSigned(out var password2);
        File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx, password2));
        File.WriteAllText(key, protector.Protect(password2));
        source = $"new self-signed certificate ({pfx})";
        return X509CertificateLoader.LoadPkcs12FromFile(pfx, password2);
    }

    private static X509Certificate2 CreateSelfSigned(out string password)
    {
        var machine = Environment.MachineName;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={machine}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(machine);
        try
        {
            var fqdn = Dns.GetHostEntry(machine).HostName;
            if (!string.Equals(fqdn, machine, StringComparison.OrdinalIgnoreCase)) names.AddDnsName(fqdn);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // No DNS entry: the short name is enough.
        }
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }
}
