using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Application.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Merit.Web.Infrastructure;

/// <summary>
/// The keyed hash and encryption for user names that matched no account (#191). The hash key is SignInLog:NameHashKey
/// (base64, 32+ bytes, the same on every web server), or EmailCodes:VerifierKey when that is set. Without either, a random
/// key is used for this process and a warning logged: hashes then don't match across restarts or servers. Encryption is
/// ASP.NET Core data protection under its own purpose, on the key ring that already protects authenticator secrets.
/// </summary>
public sealed class AttemptedNameProtection : IAttemptedNameProtection
{
    public const string KeySetting = "SignInLog:NameHashKey";

    private readonly byte[] key;
    private readonly IDataProtector protector;

    public AttemptedNameProtection(IDataProtectionProvider protection, IConfiguration configuration, ILogger<AttemptedNameProtection> logger)
    {
        protector = protection.CreateProtector("Merit.SignInLog.AttemptedUserName");
        string? configured = configuration[KeySetting] is { Length: > 0 } own ? own : configuration["EmailCodes:VerifierKey"];
        byte[]? decoded = null;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                decoded = Convert.FromBase64String(configured);
            }
            catch (FormatException)
            {
                decoded = null;
            }
        }

        if (decoded is { Length: >= 32 })
        {
            key = decoded;
        }
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
            SignInLogMessages.NoKey(logger, KeySetting);
        }
    }

    public byte[] Hash(string userName) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Normalize(userName)));

    public string Protect(string userName) => protector.Protect(userName.Trim());

    public string? Unprotect(string protectedName)
    {
        try
        {
            return protector.Unprotect(protectedName);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string Normalize(string userName) => userName.Trim().ToLowerInvariant();
}

/// <summary>
/// Writes sign-in events (#191) with what the request knows: client address (after trusted forwarded headers), the
/// forwarded chain, user agent, trace id, the sign-in attempt and the session. A name that matched no account is stored
/// only hashed and encrypted, and logged only as its hash. Recording never fails the action it describes: a failure is
/// logged and swallowed.
/// </summary>
public sealed class SignInEventRecorder(IHttpContextAccessor http, IAccountData data, IAttemptedNameProtection names, IConfiguration configuration,
                                        ILogger<SignInEventRecorder> logger) : ISignInEvents
{
    /// <summary>HttpContext.Items key for the sign-in attempt id during the password step, before the pending-MFA cookie exists.</summary>
    public const string AttemptItem = "merit.signInAttempt";

    /// <summary>Where the pending-MFA cookie keeps the attempt id: its properties, so antiforgery tokens (bound to the claims) still match.</summary>
    public const string AttemptProperty = "merit.attempt";

    public async Task RecordAsync(SignInEvent e, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        var context = http.HttpContext;
        try
        {
            byte[]? hash = e.AttemptedUserName is { Length: > 0 } typed ? names.Hash(typed) : null;
            string? protectedName = e.AttemptedUserName is { Length: > 0 } name ? names.Protect(name) : null;
            var user = context?.User;
            Guid? appUserId = e.AppUserId ?? ClaimGuid(user, MeritClaims.UserId);
            Guid? scope = e.SessionScopeId ?? ClaimGuid(user, MeritClaims.ScopeKey);
            Guid? attempt = context?.Items[AttemptItem] as Guid? ?? await PendingAttemptAsync(context).ConfigureAwait(false);
            string? address = e.ClientAddress ?? context?.Connection.RemoteIpAddress?.ToString();
            string? forwarded = Forwarded(context);
            string? agent = context?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? Truncate(ua, 400) : null;
            string? trace = e.TraceId ?? Activity.Current?.TraceId.ToString() ?? context?.TraceIdentifier;

            await data.RecordSignInEventAsync(new SignInEventInput(e.Type, e.Step, e.Method, e.Reason, appUserId, e.ActorUserId, hash, protectedName,
                address, forwarded, agent, scope, attempt, trace), CancellationToken.None).ConfigureAwait(false);
            SignInLogMessages.Event(logger, e.Type, e.Step, e.Method, e.Reason, appUserId, e.ActorUserId, hash is null ? null : Convert.ToHexString(hash)[..16],
                address, agent, attempt, trace);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            SignInLogMessages.Failed(logger, ex, e.Type);
        }
    }

    private string? Forwarded(HttpContext? context)
    {
        if (context is null || configuration.GetSection("Merit:ForwardedHeaders:KnownProxies").Get<string[]>() is not { Length: > 0 })
        {
            return null;
        }

        // UseForwardedHeaders moves the values it consumed to X-Original-For and leaves the rest in X-Forwarded-For.
        string chain = string.Join(", ", new[] { context.Request.Headers["X-Original-For"].ToString(), context.Request.Headers["X-Forwarded-For"].ToString() }
            .Where(v => v.Length > 0));
        return chain.Length == 0 ? null : Truncate(chain, 500);
    }

    /// <summary>The attempt id of the pending-MFA cookie, on the second-factor pages.</summary>
    private static async Task<Guid?> PendingAttemptAsync(HttpContext? context)
    {
        if (context is null || !context.Request.Cookies.ContainsKey("__Host-merit-mfa"))
        {
            return null;
        }

        var pending = await context.AuthenticateAsync(AuthSchemes.PendingMfa).ConfigureAwait(false);
        return Guid.TryParse(pending.Properties?.GetString(AttemptProperty), CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static Guid? ClaimGuid(ClaimsPrincipal? user, string type)
        => Guid.TryParse(user?.FindFirstValue(type), CultureInfo.InvariantCulture, out var id) ? id : null;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>
/// An idle session ends in the browser: the cookie's ticket expires and the cookie handler just treats the request as
/// signed out. This reads the expired ticket once, records sessionExpired (idle) for its session, ends the session scope
/// in the database and drops the cookie, so it is recorded once (#191).
/// </summary>
public sealed class SessionExpiryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IOptionsMonitor<CookieAuthenticationOptions> cookieOptions, TimeProvider time)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            var options = cookieOptions.Get(AuthSchemes.Session);
            string? cookie = options.Cookie.Name is { } name ? options.CookieManager.GetRequestCookie(http, name) : null;
            if (!string.IsNullOrEmpty(cookie) && options.TicketDataFormat.Unprotect(cookie) is { } ticket
                && ticket.Properties.ExpiresUtc is { } expires && expires <= time.GetUtcNow()
                && Guid.TryParse(ticket.Principal.FindFirstValue(MeritClaims.ScopeKey), CultureInfo.InvariantCulture, out var scope)
                && Guid.TryParse(ticket.Principal.FindFirstValue(MeritClaims.UserId), CultureInfo.InvariantCulture, out var user))
            {
                var services = http.RequestServices;
                await services.GetRequiredService<ISignInEvents>().RecordAsync(new SignInEvent(SignInEventTypes.SessionExpired, SignInSteps.Session,
                    Reason: "idle", AppUserId: user, SessionScopeId: scope), http.RequestAborted).ConfigureAwait(false);
                await services.GetRequiredService<AccountService>().SignOutAsync(scope, http.RequestAborted).ConfigureAwait(false);
                options.CookieManager.DeleteCookie(http, options.Cookie.Name!, options.Cookie.Build(http));
            }
        }

        await next(http).ConfigureAwait(false);
    }
}

internal static partial class SignInLogMessages
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Setting} is not set: attempted user names are hashed with a key for this process only, so hashes won't match across restarts or servers")]
    public static partial void NoKey(ILogger logger, string setting);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in event {EventType} step {Step} method {Method} reason {Reason} user {AppUserId} actor {ActorUserId} name hash {NameHash} address {ClientAddress} agent {UserAgent} attempt {AttemptId} trace {TraceId}")]
    public static partial void Event(ILogger logger, string eventType, string? step, string? method, string? reason, Guid? appUserId, Guid? actorUserId,
                                     string? nameHash, string? clientAddress, string? userAgent, Guid? attemptId, string? traceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sign-in event {EventType} could not be recorded")]
    public static partial void Failed(ILogger logger, Exception exception, string eventType);
}
