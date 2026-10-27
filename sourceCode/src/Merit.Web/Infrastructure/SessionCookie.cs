using System.Security.Claims;
using Merit.Application.Data;
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

        await http.SignInAsync(AuthSchemes.Session,
            new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Session, MeritClaims.UserName, null)),
            new AuthenticationProperties { IsPersistent = false, IssuedUtc = DateTimeOffset.UtcNow });
    }
}
