using CvHub.Data;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace CvHub.Services;

public class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";
    /// <summary>From address; defaults to <see cref="User"/>. Should be the Gmail address owning the app password.</summary>
    public string From { get; set; } = "";
    /// <summary>Display name on outgoing mail.</summary>
    public string FromName { get; set; } = "CvHub";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Pass);
}

/// <summary>
/// Identity email sender for account confirmation / password reset, delivered over Gmail SMTP
/// (STARTTLS on port 587, app-password auth). Sends happen on a background worker so a slow
/// SMTP answer never blocks (or breaks) registration; when Gmail rejects the send, the error
/// is logged with the full link so support can still help the user. Emails are branded HTML
/// with a plain-text alternative for deliverability.
/// </summary>
internal sealed class EmailSender(ILogger<EmailSender> logger, IOptions<SmtpOptions> options) : IEmailSender<ApplicationUser>
{
    private SmtpOptions O => options.Value;

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        QueueAsync(email, "Confirm your CvHub email", BuildBody(
            heading: "Confirm your email",
            intro: "Welcome to CvHub! Please confirm your email address to activate your account.",
            link: confirmationLink,
            buttonText: "Confirm my email",
            note: "This link expires for security reasons. If it has expired, request a new one from the login page."));

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        QueueAsync(email, "Reset your CvHub password", BuildBody(
            heading: "Reset your password",
            intro: "We received a request to reset your CvHub password.",
            link: resetLink,
            buttonText: "Choose a new password",
            note: "If you did not request this, you can safely ignore this email — your password stays unchanged."));

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        QueueAsync(email, "Your CvHub password reset code", BuildBody(
            heading: "Reset your password",
            intro: "Use this code to continue resetting your CvHub password:",
            code: resetCode,
            note: "If you did not request this, you can safely ignore this email."));

    /// <summary>Fire-and-forget on the thread pool: registration must never wait on Gmail.</summary>
    private Task QueueAsync(string email, string subject, (string Html, string Text) body)
    {
        if (!O.IsConfigured)
        {
            logger.LogWarning("[Email] SMTP not configured — cannot deliver mail to {Email} ({Subject}). " +
                              "Configure the Smtp section to enable account confirmation emails.", email, subject);
            return Task.CompletedTask;
        }
        // Capture values: scoped options may be disposed before the background task runs.
        var host = O.Host; var port = O.Port; var user = O.User; var pass = O.Pass;
        var from = string.IsNullOrWhiteSpace(O.From) ? O.User : O.From;
        var fromName = string.IsNullOrWhiteSpace(O.FromName) ? "CvHub" : O.FromName;

        return Task.Run(async () =>
        {
            try
            {
                var msg = new MimeMessage();
                msg.From.Add(new MailboxAddress(fromName, from));
                msg.To.Add(MailboxAddress.Parse(email));
                msg.Subject = subject;
                var builder = new MimeKit.BodyBuilder { HtmlBody = body.Html, TextBody = body.Text };
                msg.Body = builder.ToMessageBody();

                using var client = new SmtpClient();
                client.Timeout = 20_000;
                await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(user, pass);
                await client.SendAsync(msg);
                await client.DisconnectAsync(true);
                logger.LogInformation("[Email] Sent -> {Email} :: {Subject}", email, subject);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var linkMatch = System.Text.RegularExpressions.Regex.Match(body.Html, "href=\"([^\"]+)\"");
                    if (linkMatch.Success)
                        logger.LogDebug("[Email] link: {Link}", linkMatch.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Email] FAILED to deliver '{Subject}' to {Email}. " +
                    "For Gmail: Host=smtp.gmail.com, Port=587, User=<gmail address>, Pass=<16-char app password>. " +
                    "If the user is blocked by the failed send, ask them to use 'Resend email confirmation'.",
                    subject, email);
            }
        });
    }

    private static (string Html, string Text) BuildBody(string heading, string intro,
        string? link = null, string? buttonText = null, string? code = null, string? note = null)
    {
        // The link is placed in an href attribute AND shown as text: attribute-encode once,
        // and reject relative links outright — an email client can't click "Account/Confirm".
        var linkHtml = "";
        if (link is not null)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException(
                    $"Confirmation link must be an absolute http(s) URL (got: '{link}'). " +
                    "Build it with NavigationManager.ToAbsoluteUri so the email contains a clickable address.");
            var encoded = HtmlEncoder.Default.Encode(link);
            linkHtml =
                $"<a href=\"{encoded}\" style=\"display:inline-block;background:#4f46e5;color:#ffffff;text-decoration:none;font-weight:600;padding:12px 24px;border-radius:8px;margin:18px 0;\">{buttonText ?? "Open"}</a>" +
                $"<p style=\"color:#64748b;font-size:13px;word-break:break-all;\">Or paste this link into your browser:<br>{encoded}</p>";
        }
        var codeHtml = code is null ? "" :
            $"<p style=\"font-size:22px;letter-spacing:4px;font-weight:700;background:#f1f5f9;padding:12px 18px;border-radius:8px;display:inline-block;\">{HtmlEncoder.Default.Encode(code)}</p>";
        var noteHtml = note is null ? "" : $"<p style=\"color:#64748b;font-size:13px;\">{note}</p>";

        var html = new System.Text.StringBuilder();
        html.Append("<!DOCTYPE html><html><body style=\"margin:0;background:#f8fafc;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;\">")
            .Append("<div style=\"max-width:520px;margin:0 auto;padding:32px 24px;\">")
            .Append("<div style=\"text-align:center;margin-bottom:24px;\">")
            .Append("<span style=\"display:inline-block;background:#4f46e5;color:#fff;font-weight:700;border-radius:14px;padding:10px 16px;\">CV</span>")
            .Append("<span style=\"font-size:20px;font-weight:700;color:#0f172a;margin-left:6px;\">CvHub</span></div>")
            .Append("<div style=\"background:#ffffff;border:1px solid #e2e8f0;border-radius:14px;padding:28px;\">")
            .Append($"<h1 style=\"font-size:20px;color:#0f172a;margin:0 0 12px;\">{System.Net.WebUtility.HtmlEncode(heading)}</h1>")
            .Append($"<p style=\"color:#334155;font-size:15px;line-height:1.6;margin:0 0 8px;\">{System.Net.WebUtility.HtmlEncode(intro)}</p>")
            .Append(codeHtml)
            .Append(linkHtml)
            .Append(noteHtml)
            .Append("</div>")
            .Append("<p style=\"text-align:center;color:#94a3b8;font-size:12px;margin-top:18px;\">Sent by CvHub — you received this because an account was created with this address.</p>")
            .Append("</div></body></html>");

        var text = $"{heading}\n\n{intro}\n{(code is null ? "" : $"\nCode: {code}")}" +
                  (link is null ? "" : $"\n\nOpen this link:\n{link}") + $"\n\n{(note ?? "")}\n\n— CvHub";
        return (html.ToString(), text);
    }
}
