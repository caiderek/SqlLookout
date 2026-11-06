using SqlLookout.Alerts;

namespace SqlLookout.Service;

/// <summary>Builds the sender for the channels that are set up: email, Teams, or both.</summary>
public static class AlertSenders
{
    /// <param name="none">What to use when no channel is set up (the console when running headless, a sender that records nothing in the web mode).</param>
    public static IAlertSender For(AlertSettings alerts, Func<IAlertSender> none, Action<string>? log = null)
    {
        var channels = new List<IAlertSender>();
        if (alerts.Email is { } e) channels.Add(new SmtpAlertSender(e, e.ResolvePassword()));
        if (alerts.Teams is { } t) channels.Add(new TeamsAlertSender(t));
        return channels.Count switch
        {
            0 => none(),
            1 => channels[0],
            _ => new CompositeAlertSender(channels, log),
        };
    }
}
