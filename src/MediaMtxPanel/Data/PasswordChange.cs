using Microsoft.AspNetCore.Identity;

namespace MediaMtxPanel.Data;

/// <summary>
/// One-time passwords: a user carrying <see cref="ClaimType"/> is sent to the change-password page
/// until they pick their own password.
/// </summary>
public static class PasswordChange
{
    public const string ClaimType = "mediamtx-panel:must-change-password";
    public const string PagePath = "/Account/Manage/ChangePassword";

    public static IApplicationBuilder UseRequiredPasswordChange(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.HasClaim(c => c.Type == ClaimType) && IsPageRequest(context.Request))
            {
                context.Response.Redirect($"{context.Request.PathBase}{PagePath}");
                return;
            }
            await next();
        });

    // Only page navigations: static files, the Blazor circuit, APIs, the account pages and public watch pages keep working.
    private static bool IsPageRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
            return false;
        var path = request.Path.Value ?? "/";
        string[] open = ["/Account", "/_", "/api", "/watch"];
        return !open.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !Path.HasExtension(path);
    }

    public static async Task ClearAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        var claims = (await users.GetClaimsAsync(user)).Where(c => c.Type == ClaimType).ToList();
        if (claims.Count > 0)
            await users.RemoveClaimsAsync(user, claims);
    }
}
