using System.Net;
using System.Net.Mail;

namespace SqlLookout.Alerts;

public class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string? User { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
}

public sealed class SmtpAlertSender(SmtpSettings settings, string? password) : IAlertSender
{
    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseSsl, Timeout = 30_000 };
        if (!string.IsNullOrEmpty(settings.User)) client.Credentials = new NetworkCredential(settings.User, password);

        using var message = new MailMessage { From = new MailAddress(settings.From), Subject = subject, Body = body };
        foreach (var to in settings.To) message.To.Add(to);
        await client.SendMailAsync(message, ct);
    }
}
