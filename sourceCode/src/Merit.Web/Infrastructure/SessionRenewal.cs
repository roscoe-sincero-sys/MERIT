using Merit.Application.Security;
using Merit.Application.Services;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Starting and renewing the database session scope behind the session cookie, in one place (issue #21). A renewal creates
/// the new scope first, then ends the old one and rewrites the cookie, so a failure leaves the current session working.
/// </summary>
public sealed class SessionRenewal(AccountService accounts, ICurrentSession session, IConfiguration configuration)
{
    public const string LifetimeSetting = "Session:LifetimeHours";

    /// <summary>How long a session scope lives at most, idle or not (Session:LifetimeHours, default 12).</summary>
    public TimeSpan Lifetime => TimeSpan.FromHours(Math.Max(1, configuration.GetValue(LifetimeSetting, 12)));

    /// <summary>A new session for a person who has just signed in: always the master profile.</summary>
    public async Task SignInAsync(HttpContext http, Guid userId, CancellationToken ct)
    {
        var scope = await accounts.CreateSessionAsync(userId, Lifetime, ct: ct).ConfigureAwait(false);
        await SessionCookie.SignInAsync(http, scope.Scope).ConfigureAwait(false);
    }

    /// <summary>Replaces the current session with one for this profile, view-as user and working tenant.</summary>
    public async Task RenewAsync(HttpContext http, Guid? userProfileId, Guid? viewAsUserId, Guid? actingTenantId, CancellationToken ct)
    {
        var me = session.RequireUserId();
        var scope = await accounts.CreateSessionAsync(me, Lifetime, userProfileId, viewAsUserId, actingTenantId, ct)
            .ConfigureAwait(false);
        if (session.ScopeKey is Guid old)
        {
            await accounts.SignOutAsync(old, ct).ConfigureAwait(false);
        }

        await SessionCookie.SignInAsync(http, scope.Scope).ConfigureAwait(false);
    }

    /// <summary>The same profile, view and tenant in a fresh scope, so tenant lists resolved at sign-in are read again.</summary>
    public Task RefreshAsync(HttpContext http, CancellationToken ct)
        => RenewAsync(http, session.ProfileId, session.ViewAsUserId, session.ActingTenantId, ct);
}
