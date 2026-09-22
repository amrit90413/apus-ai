namespace Gateway.Api.Security;

/// <summary>
/// Response security headers.
///
/// The gateway serves JSON and SSE, not HTML, but its OAuth redirect endpoints are
/// reached by a browser — so framing, sniffing and referrer leakage all need closing.
/// The dashboard is served by Next.js behind the same NGINX and carries its own CSP;
/// this is the API's own floor, applied to every response including errors.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _hsts;

    public SecurityHeadersMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        // Only meaningful over TLS, and harmful to send from a plain-HTTP dev instance.
        _hsts = config.GetValue("Security:Hsts", true);
    }

    public Task Invoke(HttpContext ctx)
    {
        var headers = ctx.Response.Headers;

        // API responses are never framed and never scripted, so the tightest policy
        // that still allows a redirect response is the right one.
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=()";

        if (_hsts && ctx.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

        return _next(ctx);
    }
}
