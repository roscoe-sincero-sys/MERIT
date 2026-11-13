using System.Security.Claims;
using Merit.Application.Data;
using Merit.Application.Reference;
using Microsoft.AspNetCore.Authentication;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Writes the session cookie from a resolved session scope: at sign-in, and again at every profile switch or view-as,
/// which each create a new scope (the old one is revoked by the caller). The claims carry the scope key the database
/// filters by, the permissions the policies check, and the profile shown in the header.
/// </summary>
public static class SessionCookie
{
    public static async Task SignInAsync(HttpContext http, SessionScopeResult scope)
    {
        var s = scope.Summary;
        var claims = new List<Claim>
        {
            new(MeritClaims.UserId, s.AppUserId.ToString()),
            new(MeritClaims.UserName, s.UserName),
            new(MeritClaims.DisplayName, s.DisplayName),
            new(MeritClaims.ScopeKey, s.SessionScopeId.ToString()),
            new(MeritClaims.RootTenantId, s.RootTenantId.ToString()),
            new(MeritClaims.ActingTenantId, s.ActingTenantId.ToString()),
            new(MeritClaims.ActingTenantName, s.ActingTenantName ?? string.Empty),
            new(MeritClaims.SecurityStamp, s.SecurityStamp.ToString()),
            new(MeritClaims.ProfileName, s.ProfileName),
            new(MeritClaims.TimeZone, s.TimeZone),
            new(MeritClaims.IsPlatformAdmin, s.IsPlatformAdmin ? "1" : "0"),
        };
        if (s.PasswordChangeRequired)
        {
            claims.Add(new Claim(MeritClaims.PasswordChangeRequired, "1"));
        }

        if (s.UserProfileId is Guid profile)
        {
            claims.Add(new Claim(MeritClaims.ProfileId, profile.ToString()));
        }

        if (s.ViewAsUserId is Guid viewAs)
        {
            claims.Add(new Claim(MeritClaims.ViewAsUserId, viewAs.ToString()));
            claims.Add(new Claim(MeritClaims.ViewAsName, s.ViewAsDisplayName ?? string.Empty));
        }

        claims.AddRange(scope.Permissions.Select(p => new Claim(MeritClaims.Permission, p)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Session, MeritClaims.UserName, null));
        var issued = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        // Issue #289: the data root's sessionTimeoutMinutes setting is the idle timeout of sessions started after the
        // change. Sliding renewal keeps the ticket's own lifetime, so setting it here is enough; with no valid setting
        // the deployment default (Session:IdleTimeoutMinutes) applies.
        int idleMinutes = IdleMinutes(s.SessionIdleMinutes, http.RequestServices.GetRequiredService<IConfiguration>());
        await http.SignInAsync(AuthSchemes.Session,
            principal,
            new AuthenticationProperties { IsPersistent = false, IssuedUtc = issued, ExpiresUtc = issued.AddMinutes(idleMinutes) });
        // A view rendered on this response must mint antiforgery tokens for the newly signed-in identity.
        http.User = principal;
    }

    /// <summary>The idle timeout for a new session: the root's setting when it is within 5 to 60 minutes, else the deployment default.</summary>
    public static int IdleMinutes(int? rootSetting, IConfiguration configuration)
        => rootSetting is >= SettingCatalogue.SessionTimeoutMinimum and <= SettingCatalogue.SessionTimeoutMaximum
            ? rootSetting.Value
            : configuration.GetValue("Session:IdleTimeoutMinutes", 20);
}
