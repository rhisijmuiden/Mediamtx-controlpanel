using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services.Localization;
using MediaMtxPanel.Services.Payments;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

/// <summary>
/// Sign-in for viewers who bought tickets: no password, but a one-time code mailed to the address they
/// paid with. Viewers get their own cookie, separate from panel accounts, so a viewer never becomes a panel user.
/// </summary>
public class ViewerAccounts(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PaywallService paywall,
    MailService mail,
    PanelSettingsStore settings,
    TextLocalizer l,
    ILogger<ViewerAccounts> logger)
{
    public const string Scheme = "Viewer";
    public const string CookieName = ".MediaMtxPanel.Viewer";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    private const int MaxCodesPerHour = 5;
    private const int MaxAttempts = 5;

    public bool CanSignIn => mail.IsConfigured;

    /// <summary>The signed-in viewer's e-mail address, or null.</summary>
    public static async Task<string?> EmailAsync(HttpContext? http)
    {
        if (http is null)
            return null;
        var result = await http.AuthenticateAsync(Scheme);
        return result.Succeeded ? result.Principal.FindFirstValue(ClaimTypes.Email) : null;
    }

    /// <summary>
    /// Mails a sign-in code when the address has tickets. Always reports success to the caller, so the
    /// form does not reveal who bought tickets; only rate limits are reported.
    /// </summary>
    public async Task<bool> SendCodeAsync(string? email, CancellationToken ct = default)
    {
        email = PaywallService.NormalizeEmail(email);
        if (email is null || !CanSignIn)
            return true;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        if (await db.ViewerLoginCodes.CountAsync(c => c.Email == email && c.CreatedAt > now.AddHours(-1), ct) >= MaxCodesPerHour)
            return false;
        if (!await paywall.HasTicketsAsync(email, ct))
            return true;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
        db.ViewerLoginCodes.RemoveRange(db.ViewerLoginCodes.Where(c => c.ExpiresAt < now.AddDays(-1)));
        db.ViewerLoginCodes.Add(new ViewerLoginCode { Email = email, CodeHash = Hash(email, code), CreatedAt = now, ExpiresAt = now + CodeLifetime });
        await db.SaveChangesAsync(ct);

        var app = WebUtility.HtmlEncode(settings.Current.AppName);
        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:480px;margin:auto;padding:24px;color:#1f2937">
              <h2 style="margin:0 0 12px">{app}</h2>
              <p>{WebUtility.HtmlEncode(l["Use this code to sign in and see your tickets:"])}</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:8px;background:#eef2ff;color:#3730a3;padding:16px;border-radius:12px;text-align:center">{code}</p>
              <p style="color:#6b7280;font-size:13px">{WebUtility.HtmlEncode(l["The code works for 15 minutes. Didn't ask for it? Then you can ignore this email."])}</p>
            </div>
            """;
        try
        {
            await mail.SendAsync(email, l["Your sign-in code: {0}", code], html, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Sending a viewer sign-in code failed: {Message}", ex.Message);
        }
        return true;
    }

    /// <summary>Checks a mailed code; on success the viewer is signed in for 30 days.</summary>
    public async Task<bool> VerifyAndSignInAsync(HttpContext http, string? email, string? code, CancellationToken ct = default)
    {
        email = PaywallService.NormalizeEmail(email);
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (email is null || code.Length != 6)
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var entry = await db.ViewerLoginCodes
            .Where(c => c.Email == email && !c.Used && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (entry is null || entry.Attempts >= MaxAttempts)
            return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.CodeHash), Encoding.ASCII.GetBytes(Hash(email, code))))
        {
            entry.Attempts++;
            await db.SaveChangesAsync(ct);
            return false;
        }
        entry.Used = true;
        await db.SaveChangesAsync(ct);

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Email, email)], Scheme);
        await http.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
        return true;
    }

    internal static string Hash(string email, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{email}\n{code}")));
}
