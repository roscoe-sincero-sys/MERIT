using System.Security.Claims;
using System.Text.Json;
using Merit.Application.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Recovery codes shown at enrollment but not yet acknowledged (issue #156). Sign-in is already complete at that step, so
/// until the person says they have saved the codes, every other page leads back to it, showing the same codes again.
/// The codes ride in an encrypted, HttpOnly cookie on the browser they were issued to, and only for half an hour; they
/// are never stored unencrypted, and the database keeps only their hashes as before.
/// </summary>
public sealed class RecoveryCodesPending(IDataProtectionProvider protection)
{
    public const string CookieName = "merit.recovery-pending";
    public const string StepPath = "/account/recovery";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("Merit.RecoveryCodesPending").ToTimeLimitedDataProtector();

    private sealed record Payload(Guid UserId, IReadOnlyList<string> Codes, string ReturnUrl);

    public void Hold(HttpResponse response, Guid userId, IReadOnlyList<string> codes, string returnUrl)
        => response.Cookies.Append(CookieName,
            protector.Protect(JsonSerializer.Serialize(new Payload(userId, codes, returnUrl)), Lifetime),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = Lifetime, IsEssential = true });

    /// <summary>The codes still waiting for the signed-in person, or null.</summary>
    public (IReadOnlyList<string> Codes, string ReturnUrl)? Waiting(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value)
            || !Guid.TryParse(http.User.FindFirstValue(MeritClaims.UserId), out var userId))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(value, out _));
            return payload is not null && payload.UserId == userId ? (payload.Codes, payload.ReturnUrl) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static void Release(HttpResponse response) => response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = true, SameSite = SameSiteMode.Strict });
}

/// <summary>Sends a signed-in person back to their unacknowledged recovery codes before anything else (issue #156).</summary>
public sealed class RecoveryCodesGateMiddleware(RequestDelegate next)
{
    private static readonly string[] Allowed = [RecoveryCodesPending.StepPath, "/account/signout", "/lib", "/css", "/js", "/favicon.svg", "/home/status", "/home/error"];

    public async Task InvokeAsync(HttpContext http, RecoveryCodesPending pending)
    {
        if (http.User.Identity?.IsAuthenticated == true && http.Request.Cookies.ContainsKey(RecoveryCodesPending.CookieName)
            && !Allowed.Any(p => http.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            if (pending.Waiting(http) is null)
            {
                // Someone else's, expired or unreadable: it holds nothing for this person.
                RecoveryCodesPending.Release(http.Response);
            }
            else if (HttpMethods.IsGet(http.Request.Method) && !http.Request.Headers.ContainsKey("HX-Request")
                     && !http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.Redirect(RecoveryCodesPending.StepPath);
                return;
            }
            else
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsync("Save your recovery codes and acknowledge that you have saved them before continuing.").ConfigureAwait(false);
                return;
            }
        }

        await next(http).ConfigureAwait(false);
    }
}
