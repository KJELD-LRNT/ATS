namespace AssetTracker.Web.Security;

/// <summary>
/// Response hardening. <see cref="UseSecurityHeaders"/> adds browser security headers to every response (including
/// static files); <see cref="UseNoStoreForDynamicResponses"/>, placed after static files, marks every dynamic
/// response (pages, CSV exports, health) <c>Cache-Control: no-store</c>, because pages and exports carry personal data.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Strict CSP: the pages use no inline scripts, inline styles or third-party resources (only <c>/css/site.css</c>, <c>/js/site.js</c> and the self-hosted fonts under <c>/fonts</c>, covered by <c>font-src 'self'</c>).
    /// Adding an inline <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c> or <c>style=""</c> attribute will be blocked by browsers.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        var h = context.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = ContentSecurityPolicy;
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        return next(context);
    });

    public static IApplicationBuilder UseNoStoreForDynamicResponses(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            var current = h.CacheControl.ToString();
            if (!current.Contains("no-store", StringComparison.OrdinalIgnoreCase))
                h.CacheControl = "no-store";
            h.Pragma = "no-cache";
            return Task.CompletedTask;
        });
        return next(context);
    });
}
