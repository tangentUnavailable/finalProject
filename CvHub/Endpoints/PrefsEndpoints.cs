using CvHub.Shared;

namespace CvHub.Endpoints;

/// <summary>Theme + language preference endpoints (cookie-backed; choice is persisted).</summary>
public static class PrefsEndpoints
{
    public static void MapPrefsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/prefs/theme/{t}", (string t, HttpContext ctx) =>
        {
            if (t is "light" or "dark")
            {
                ctx.Response.Cookies.Append("cv_theme", t, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                });
            }
            return Results.LocalRedirect(Back(ctx));
        });

        app.MapGet("/prefs/lang/{l}", (string l, HttpContext ctx) =>
        {
            if (l is "en" or "es")
            {
                ctx.Response.Cookies.Append("cv_lang", l, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                });
            }
            return Results.LocalRedirect(Back(ctx));
        });

        app.MapGet("/health", () => Results.Ok("ok"));
    }

    /// <summary>Local path+query of the Referer (absolute Referer URLs are rejected by LocalRedirect).</summary>
    private static string Back(HttpContext ctx)
    {
        var referer = ctx.Request.Headers.Referer.ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
            (uri.Port == 0 || uri.Port == ctx.Request.Host.Port))
        {
            return uri.PathAndQuery;
        }
        return "/";
    }
}
