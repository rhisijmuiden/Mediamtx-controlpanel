using MediaMtxPanel.Services.Localization;
using Microsoft.AspNetCore.Localization;

namespace MediaMtxPanel.Endpoints;

public static class LanguageEndpoint
{
    /// <summary>
    /// GET /api/language?code=nl&amp;returnUrl=/streams stores the visitor's language in a cookie and goes back.
    /// Under /api so that it also works while a one-time password still has to be changed.
    /// </summary>
    public static void MapLanguageEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/language", (HttpContext context, string? code, string? returnUrl) =>
        {
            var language = TextLocalizer.Normalize(code) ?? TextLocalizer.DefaultLanguage;
            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(language)),
                new CookieOptions { Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/", Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
            var target = string.IsNullOrEmpty(returnUrl) ? "/" : "/" + returnUrl.TrimStart('/');
            // Only local paths: never redirect to another site. "~" keeps the redirect under the path base.
            return Results.LocalRedirect("~" + (target.StartsWith("//") || target.StartsWith("/\\") ? "/" : target));
        }).AllowAnonymous();
}
