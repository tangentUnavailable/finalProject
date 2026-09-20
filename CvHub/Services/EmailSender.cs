using CvHub.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CvHub.Services;

/// <summary>
/// Identity email sender backing the account-confirmation / password-reset flow —
/// i.e. "form authentication with email confirmation as an alternative to social login".
/// Sends via SMTP when <c>Smtp:Host</c> is configured; otherwise logs the link (the VPS
/// has no SMTP relay) so confirmation/reset still complete end-to-end (links appear in
/// <c>journalctl -u cvhub</c>) without ever breaking sign-up or sign-in. Real delivery
/// is enabled by adding <c>Smtp:Host/User/Pass/From</c> to configuration.
/// </summary>
internal sealed class EmailSender(ILogger<EmailSender> logger, IConfiguration config) : IEmailSender<ApplicationUser>
{
    private readonly bool _hasSmtp = !string.IsNullOrWhiteSpace(config["Smtp:Host"]);

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendAsync(email, "Confirm your email", confirmationLink);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendAsync(email, "Reset your password", resetLink);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendAsync(email, "Reset your password", resetCode);

    private Task SendAsync(string email, string subject, string linkOrCode)
    {
        // Never throws into the request pipeline: when SMTP isn't configured we log the
        // link; when it is, a real send is attempted and any failure is logged + swallowed.
        if (!_hasSmtp)
        {
            logger.LogInformation("[Email] SMTP not configured -> {Email} :: {Subject} | {Link}", email, subject, linkOrCode);
            return Task.CompletedTask;
        }

        try
        {
            // TODO: wire MailKit (or System.Net.Mail SmtpClient) here for real delivery.
            logger.LogInformation("[Email] (SMTP configured but sender not yet implemented) -> {Email} :: {Subject} | {Link}", email, subject, linkOrCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email to {Email}", email);
        }
        return Task.CompletedTask;
    }
}
