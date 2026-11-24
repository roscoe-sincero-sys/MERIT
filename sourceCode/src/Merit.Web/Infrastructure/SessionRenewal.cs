using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Application.Data;
using Microsoft.AspNetCore.Authentication;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Starting and renewing the database session scope behind the session cookie, in one place (issue #21). A renewal creates
/// the new scope first, then ends the old one and rewrites the cookie, so a failure leaves the current session working.
/// </summary>
public sealed class SessionRenewal(AccountService accounts, ICurrentSession session, IConfiguration configuration, ISignInEvents events,
                                   ScreenUsageRecorder usage)
{
    public const string LifetimeSetting = "Session:LifetimeHours";

    /// <summary>How long a session scope lives at most, idle or not (Session:LifetimeHours, default 12).</summary>
    public TimeSpan Lifetime => TimeSpan.FromHours(Math.Max(1, configuration.GetValue(LifetimeSetting, 12)));

    /// <summary>A new session for a person who has just signed in: always the master profile.</summary>
    public async Task SignInAsync(HttpContext http, Guid userId, CancellationToken ct)
    {
        var scope = await accounts.CreateSessionAsync(userId, Lifetime, ct: ct).ConfigureAwait(false);
        await SessionCookie.SignInAsync(http, scope.Scope).ConfigureAwait(false);
        await events.RecordAsync(new SignInEvent(SignInEventTypes.SessionStarted, SignInSteps.Session, AppUserId: userId,
            SessionScopeId: scope.Scope.Summary.SessionScopeId), ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the current session with one for this profile, view-as user and working tenant.</summary>
    public async Task RenewAsync(HttpContext http, Guid? userProfileId, Guid? viewAsUserId, Guid? actingTenantId, CancellationToken ct)
    {
        var me = session.RequireUserId();
        var lifetime = Lifetime;
        if (session.ScopeKey is Guid currentScope)
        {
            var stampClaim = http.User.FindFirst(MeritClaims.SecurityStamp)?.Value;
            if (!Guid.TryParse(stampClaim, out var stamp) || !await accounts.IsSessionValidAsync(currentScope, stamp, ct).ConfigureAwait(false))
            {
                await http.SignOutAsync(AuthSchemes.Session).ConfigureAwait(false);
                throw new Merit.Application.Errors.BusinessRuleException("Your session has ended. Please sign in again.");
            }

            if (await accounts.SessionStateAsync(currentScope, ct).ConfigureAwait(false) is { } state)
            {
                var remaining = state.ExpiresUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await http.SignOutAsync(AuthSchemes.Session).ConfigureAwait(false);
                    throw new Merit.Application.Errors.BusinessRuleException("Your session has expired. Please sign in again.");
                }

                lifetime = remaining < lifetime ? remaining : lifetime;
            }
        }

        var scope = await accounts.CreateSessionAsync(me, lifetime, userProfileId, viewAsUserId, actingTenantId, ct)
            .ConfigureAwait(false);
        if (session.ScopeKey is Guid old)
        {
            // A profile/view/working-tenant change replaces access, not the logical signed-in visit.
            await usage.ContinueAsync(scope.Scope.Summary.SessionScopeId, ct).ConfigureAwait(false);
            await accounts.SignOutAsync(old, ct).ConfigureAwait(false);
        }

        await SessionCookie.SignInAsync(http, scope.Scope).ConfigureAwait(false);
    }

    /// <summary>The same profile, view and tenant in a fresh scope, so tenant lists resolved at sign-in are read again.</summary>
    public Task RefreshAsync(HttpContext http, CancellationToken ct)
        => RenewAsync(http, session.ProfileId, session.ViewAsUserId, session.ActingTenantId, ct);
}
