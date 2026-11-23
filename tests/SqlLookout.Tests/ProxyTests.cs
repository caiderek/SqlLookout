using System.Net;
using System.Security.Cryptography.X509Certificates;
using SqlLookout.Web;

namespace SqlLookout.Tests;

/// <summary>Running behind a reverse proxy on the same machine, such as IIS with ARR.</summary>
public class ProxyTests : IAsyncLifetime
{
    private const string PublicHost = "sqllookout.example.com";
    private WebFixture _web = null!;

    public async Task InitializeAsync() => _web = await WebFixture.StartAsync();
    public async Task DisposeAsync() => await _web.DisposeAsync();

    private async Task<HttpResponseMessage> SendAsync(string path, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", PublicHost);
        if (forwardedFor is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return await _web.Client.SendAsync(request);
    }

    [Fact]
    public async Task Sign_in_redirect_uses_the_host_name_the_proxy_was_called_by()
    {
        await _web.SignInAsAdminAsync();
        _web.ForgetCookies();
        _web.RemoteAddress = IPAddress.Loopback;

        var home = await SendAsync("/", "10.1.2.3");

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.StartsWith($"https://{PublicHost}/login", home.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Forwarded_headers_from_another_machine_are_ignored()
    {
        await _web.SignInAsAdminAsync();
        _web.ForgetCookies();
        _web.RemoteAddress = IPAddress.Parse("10.9.9.9");

        var home = await SendAsync("/", "127.0.0.1");

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.DoesNotContain(PublicHost, home.Headers.Location!.ToString());
    }

    [Fact]
    public async Task First_time_setup_through_the_proxy_is_refused_for_a_remote_client()
    {
        _web.RemoteAddress = IPAddress.Loopback;

        var remote = await SendAsync("/setup", "10.1.2.3");
        Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);

        // A client that sends its own X-Forwarded-For gets the proxy's entry added last; only that one is believed.
        var spoofed = await SendAsync("/setup", "127.0.0.1, 10.1.2.3");
        Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);

        // ARR can send the address with the client's port.
        var withPort = await SendAsync("/setup", "10.1.2.3:54321");
        Assert.Equal(HttpStatusCode.Forbidden, withPort.StatusCode);

        var local = await SendAsync("/setup", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task Mcp_key_page_shows_the_public_address()
    {
        await _web.SignInAsAdminAsync();
        _web.RemoteAddress = IPAddress.Loopback;

        var page = await SendAsync("/settings/mcp", "10.1.2.3");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains($"https://{PublicHost}/mcp", await page.Content.ReadAsStringAsync());
    }
}

public class CertificatePublicCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sqllookout-cert-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Self_signed_certificate_leaves_its_public_half_as_cert_cer()
    {
        using var first = CertificateProvider.LoadOrCreate(_dir, new FakeSecretProtector(), null, null, out _);
        var cer = Path.Combine(_dir, "cert.cer");

        Assert.True(File.Exists(cer));
        using var copy = X509CertificateLoader.LoadCertificateFromFile(cer);
        Assert.Equal(first.Thumbprint, copy.Thumbprint);
        Assert.False(copy.HasPrivateKey);
        Assert.DoesNotContain("PRIVATE", File.ReadAllText(cer));

        // A later start with the same certificate leaves the file as it is; a lost file comes back.
        var written = File.GetLastWriteTimeUtc(cer);
        using (CertificateProvider.LoadOrCreate(_dir, new FakeSecretProtector(), null, null, out _)) { }
        Assert.Equal(written, File.GetLastWriteTimeUtc(cer));
        File.Delete(cer);
        using var again = CertificateProvider.LoadOrCreate(_dir, new FakeSecretProtector(), null, null, out _);
        Assert.Equal(first.Thumbprint, again.Thumbprint);
        Assert.True(File.Exists(cer));
    }
}
