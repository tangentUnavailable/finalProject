using CvHub.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CvHub.Services;

/// <summary>
/// Identity email sender backing the account-confirmation / password-reset flow.
/// Sends via SMTP when <c>Smtp:Host</c> is configured (Gmail/Outlook/etc.);
/// otherwise logs the link so confirmation/reset still complete end-to-end
/// without ever breaking sign-up or sign-in. Real delivery needs an Smtp
/// block in configuration, e.g. for Gmail:
///   "Smtp": { "Host": "smtp.gmail.com", "Port": "587", "User": "...", "Pass": "<app-password>", "From": "..." }
/// </summary>
internal sealed class EmailSender(ILogger<EmailSender> logger, IConfiguration config) : IEmailSender<ApplicationUser>
{
    private readonly bool _hasSmtp = !string.IsNullOrWhiteSpace(config["Smtp:Host"]);
    private readonly string _host = config["Smtp:Host"] ?? "";
    private readonly string _user = config["Smtp:User"] ?? "";
    private readonly string _pass = config["Smtp:Pass"] ?? "";
    private readonly int _port = int.TryParse(config["Smtp:Port"], out var p) && p > 0 ? p : 587;

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendAsync(email, "Confirm your email", confirmationLink);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendAsync(email, "Reset your password", resetLink);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendAsync(email, "Reset your password", resetCode);

    private async Task SendAsync(string email, string subject, string linkOrCode)
    {
        // Never throws into the request pipeline: when SMTP isn't configured we
        // log the link; when it is, a real send is attempted and any failure is
        // logged + swallowed so sign-up/sign-in/reset never break.
        if (!_hasSmtp)
        {
            logger.LogInformation("[Email] SMTP not configured -> {Email} :: {Subject} | {Link}", email, subject, linkOrCode);
            return;
        }

        try
        {
            var msg = new MimeMessage();
            var from = string.IsNullOrWhiteSpace(config["Smtp:From"]) ? _user : config["Smtp:From"]!;
            msg.From.Add(MailboxAddress.Parse(from));
            msg.To.Add(MailboxAddress.Parse(email));
            msg.Subject = subject;
            msg.Body = new TextPart("html")
            {
                Text = linkOrCode.Contains("://")
                    ? $"<p>Please confirm your email:</p><p><a href=\"{linkOrCode}\">{linkOrCode}</a></p>"
                    : $"<p>Your password reset code:</p><p><code>{linkOrCode}</code></p>",
            };

            using var client = new SmtpClient();
            // Keep the request pipeline responsive — Gmail can be slow to answer.
            client.Timeout = 15_000;
            await client.ConnectAsync(_host, _port, SecureSocketOptions.StartTls);
            if (!client.IsAuthenticated && !string.IsNullOrWhiteSpace(_user))
                await client.AuthenticateAsync(_user, _pass);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);

            logger.LogInformation("[Email] Sent -> {Email} :: {Subject}", email, subject);
        }
        catch (Exception ex)
        {
            // Swallow: Identity emails must never take down a request path.
            logger.LogError(ex, "Failed to send email to {Email}", email);
        }
    }
}
