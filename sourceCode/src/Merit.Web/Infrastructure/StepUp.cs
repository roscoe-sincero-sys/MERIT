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

    public void Grant(HttpResponse response, Guid userId)
        => response.Cookies.Append(CookieName, protector.Protect(userId.ToString("N", CultureInfo.InvariantCulture), Lifetime),
            new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/account/security", MaxAge = Lifetime, IsEssential = true });

    public bool Holds(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value)
            || !Guid.TryParse(http.User.FindFirstValue(MeritClaims.UserId), out var userId))
        {
            return false;
        }

        try
        {
            return protector.Unprotect(value, out _) == userId.ToString("N", CultureInfo.InvariantCulture);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    public static void Release(HttpResponse response)
        => response.Cookies.Delete(CookieName, new CookieOptions { Path = "/account/security", Secure = true, SameSite = SameSiteMode.Strict });
}
