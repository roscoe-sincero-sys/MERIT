using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Merit.Web.Infrastructure;

/// <summary>
/// The address MERIT's own links point to (Merit:PublicBaseUrl), such as https://merit.health.example.gov. Reset links are
/// built from it, never from the request's Host header, which a requester controls (password-reset poisoning). In
/// Development, without the setting, the request's own address is used so local runs work.
/// </summary>
public static class PublicBaseUrl
{
    public const string Setting = "Merit:PublicBaseUrl";

    public static string? Resolve(HttpRequest request, IConfiguration configuration, IHostEnvironment environment)
    {
        string? configured = configuration[Setting];
        if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || environment.IsDevelopment()))
        {
            return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
        }

        return environment.IsDevelopment() ? $"{request.Scheme}://{request.Host}{request.PathBase}" : null;
    }
}

/// <summary>
/// A reset link's id and token, moved off the address bar into an encrypted cookie for the half hour the page needs. The
/// link's GET stores them and redirects to a clean /account/reset, so the token is not left in history or referrers.
/// SameSite=Lax because the link is opened from a mail client, which is a cross-site navigation.
/// </summary>
public sealed class ResetLinkCookie(IDataProtectionProvider protection)
{
    public const string CookieName = "merit.reset-link";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("Merit.PasswordReset.Link").ToTimeLimitedDataProtector();

    private sealed record Payload(Guid Id, string Token);

    public void Hold(HttpResponse response, Guid id, string token)
        => response.Cookies.Append(CookieName, protector.Protect(JsonSerializer.Serialize(new Payload(id, token)), Lifetime),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/account/reset", MaxAge = Lifetime, IsEssential = true });

    public (Guid Id, string Token)? Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var value))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(value, out _));
            return payload is null ? null : (payload.Id, payload.Token);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    public static void Release(HttpResponse response)
        => response.Cookies.Delete(CookieName, new CookieOptions { Path = "/account/reset", Secure = true, SameSite = SameSiteMode.Lax });
}

/// <summary>
/// An administrator asked for a new password at next sign-in (#190): the session carries that from the database, in the
/// encrypted session cookie, until the person sets one. Every other page leads there first. The recovery-code step
/// still comes before it for a brand-new account.
/// </summary>
public sealed class PasswordChangeGateMiddleware(RequestDelegate next)
{
    public const string StepPath = "/account/password/required";

    private static readonly string[] Allowed =
    [
        StepPath, "/account/signout", "/account/recovery", "/lib", "/css", "/js", "/favicon.svg", "/home/status", "/home/error",
    ];

    public async Task InvokeAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true && http.User.FindFirstValue(MeritClaims.PasswordChangeRequired) == "1"
            && !Allowed.Any(p => http.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            if (HttpMethods.IsGet(http.Request.Method) && !http.Request.Headers.ContainsKey("HX-Request")
                && !http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                string back = http.Request.Path + http.Request.QueryString;
                http.Response.Redirect(StepPath + "?returnUrl=" + Uri.EscapeDataString(back));
                return;
            }

            http.Response.StatusCode = StatusCodes.Status409Conflict;
            await http.Response.WriteAsync("Choose a new password before continuing.").ConfigureAwait(false);
            return;
        }

        await next(http).ConfigureAwait(false);
    }
}
