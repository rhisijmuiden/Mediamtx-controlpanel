using MailKit.Net.Smtp;
using MailKit.Security;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services.Localization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using MimeKit;

namespace MediaMtxPanel.Services;

/// <summary>
/// Sends mail through the SMTP server configured under Settings → Mail (any server: plain, STARTTLS or SSL/TLS,
/// with or without login). Also the Identity e-mail sender, so "forgot password" mails go out once mail is set up.
/// </summary>
public sealed class MailService(
    PanelSettingsStore store,
    IDataProtectionProvider dataProtection,
    TextLocalizer l,
    ILogger<MailService> logger) : IEmailSender<ApplicationUser>
{
    private IDataProtector Protector => dataProtection.CreateProtector("MediaMtxPanel.MailPassword.v1");

    public bool IsConfigured => store.Current.Mail is { Enabled: true, Host.Length: > 0, FromAddress.Length: > 0 };

    public string? Protect(string? password) => string.IsNullOrEmpty(password) ? null : Protector.Protect(password);

    public async Task SendAsync(string to, string subject, string html, MailSettings? settings = null, CancellationToken ct = default)
    {
        var mail = settings ?? store.Current.Mail ?? throw new InvalidOperationException("Mail is not configured.");
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(mail.FromName) ? store.Current.AppName : mail.FromName, mail.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 20000 };
        if (mail.AllowInvalidCertificate)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var options = mail.Security switch
        {
            MailSecurity.SslTls => SecureSocketOptions.SslOnConnect,
            MailSecurity.StartTls => SecureSocketOptions.StartTls,
            MailSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(mail.Host, mail.Port, options, ct);
        if (!string.IsNullOrEmpty(mail.UserName))
            await client.AuthenticateAsync(mail.UserName, Unprotect(mail.PasswordProtected) ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return Protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning("The stored SMTP password cannot be decrypted (data protection keys changed); enter it again under Settings → Mail.");
            return null;
        }
    }

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendIfConfiguredAsync(email, l["Confirm your email"], l["Please confirm your account by <a href='{0}'>clicking here</a>.", confirmationLink]);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendIfConfiguredAsync(email, l["Reset your password"], l["Please reset your password by <a href='{0}'>clicking here</a>.", resetLink]);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendIfConfiguredAsync(email, l["Reset your password"], l["Please reset your password using the following code: {0}", resetCode]);

    private async Task SendIfConfiguredAsync(string to, string subject, string html)
    {
        if (!IsConfigured)
        {
            logger.LogInformation("Mail to {To} not sent: no mail server configured under Settings → Mail", to);
            return;
        }
        try
        {
            await SendAsync(to, subject, html);
        }
        catch (Exception ex)
        {
            // Do not reveal mail problems to anonymous visitors (forgot-password page); log them for the administrator.
            logger.LogError(ex, "Sending mail to {To} failed", to);
        }
    }
}
