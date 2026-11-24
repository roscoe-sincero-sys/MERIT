using System.Globalization;
using System.Security.Claims;
using Merit.Application.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Merit.Web.Infrastructure;

/// <summary>
/// A recent authenticator code from the signed-in person, required before email codes can be turned on: a stolen
/// session alone cannot add a way to sign in. The proof is an encrypted, HttpOnly cookie bound to the user and good
/// for ten minutes, long enough to read the email and type its code, and to ask for another one.
/// </summary>
public sealed class StepUp(IDataProtectionProvider protection)
{
    public const string CookieName = "merit.step-up";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("Merit.StepUp.EmailCodes").ToTimeLimitedDataProtector();

    public void Grant(HttpResponse response, Guid userId, Guid? scopeKey = null)
        => response.Cookies.Append(CookieName, protector.Protect(Payload(userId, scopeKey), Lifetime),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/account/security", MaxAge = Lifetime, IsEssential = true });

    public bool Holds(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value)
            || !Guid.TryParse(http.User.FindFirstValue(MeritClaims.UserId), out var userId))
        {
            return false;
        }

        Guid? scopeKey = Guid.TryParse(http.User.FindFirstValue(MeritClaims.ScopeKey), out var sk) ? sk : null;
        try
        {
            return protector.Unprotect(value, out _) == Payload(userId, scopeKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static string Payload(Guid userId, Guid? scopeKey)
        => string.Create(CultureInfo.InvariantCulture, $"{userId:N}:{(scopeKey ?? Guid.Empty):N}");

    public static void Release(HttpResponse response)
        => response.Cookies.Delete(CookieName, new CookieOptions { Path = "/account/security", Secure = true, SameSite = SameSiteMode.Strict });
}
