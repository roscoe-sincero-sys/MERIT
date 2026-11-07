using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Rate limits for the sign-in steps, counted per client address (issue #8). One window shared by every client let a
/// single source lock everyone out and capped sign-ins for the whole system at a few a minute. Each address now has its
/// own sliding window, the password step and the code steps are counted separately, and per-account guessing is still
/// stopped by account lockout (AccountService.MaxFailedAttempts). Behind a reverse proxy, configure
/// Merit:ForwardedHeaders:KnownProxies so the address is the client's, not the proxy's.
/// </summary>
public static class SignInRateLimits
{
    /// <summary>The password step (POST /account/signin).</summary>
    public const string Password = "signin";

    /// <summary>The code steps (POST /account/mfa, /account/mfa/email and /account/enroll).</summary>
    public const string Code = "signin-code";

    /// <summary>Requests to email a one-time code (sign-in and turning email codes on). Each sends a message, so it is counted on its own.</summary>
    public const string EmailCode = "signin-email";

    public const string PasswordSetting = "Merit:SignInAttemptsPerMinute";
    public const string CodeSetting = "Merit:MfaAttemptsPerMinute";
    public const string EmailCodeSetting = "Merit:EmailCodeSendsPerMinute";

    /// <summary>Defaults sized for an office behind one address: about 30 people signing in within a minute.</summary>
    public const int DefaultPerMinute = 30;

    /// <summary>Per address; each account is also limited in the database (EmailCodes:MaxCodesPerAccount).</summary>
    public const int DefaultEmailCodesPerMinute = 10;

    public static void Configure(RateLimiterOptions o)
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy(Password, http => Partition(http, PasswordSetting, DefaultPerMinute));
        o.AddPolicy(Code, http => Partition(http, CodeSetting, DefaultPerMinute));
        o.AddPolicy(EmailCode, http => Partition(http, EmailCodeSetting, DefaultEmailCodesPerMinute));
        o.AddPolicy("screenUsage", http => RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirst(MeritClaims.UserId)?.Value ?? ClientKey(http),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Clamp(http.RequestServices.GetRequiredService<IConfiguration>().GetValue("ScreenVisit:RequestsPerMinute", 120), 1, 600),
                Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
        o.OnRejected = RejectAsync;
    }

    /// <summary>The partition key: the client address (after forwarded headers, when a proxy is configured).</summary>
    public static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> Partition(HttpContext http, string setting, int defaultPerMinute)
    {
        // Read at request time, so a deployment (or a test) can set the limit in configuration.
        int perMinute = Math.Max(1, http.RequestServices.GetRequiredService<IConfiguration>().GetValue(setting, defaultPerMinute));
        return RateLimitPartition.GetSlidingWindowLimiter(ClientKey(http), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        });
    }

    /// <summary>A page in the site layout, not a bare 429, with Retry-After for clients that read it.</summary>
    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        // Refusals are part of the sign-in record (#191): the policy that refused, from this address.
        string? policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        if (policy == "screenUsage")
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers.RetryAfter = "60";
            return; // Usage loss must not render an alarming sign-in failure or flood the security log.
        }
        await http.RequestServices.GetRequiredService<Merit.Application.Security.ISignInEvents>().RecordAsync(
            new Merit.Application.Security.SignInEvent(Merit.Application.Security.SignInEventTypes.RateLimited,
                Merit.Application.Security.SignInSteps.Request, Reason: policy), ct).ConfigureAwait(false);
        int seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry) ? (int)Math.Ceiling(retry.TotalSeconds) : 60;
        seconds = Math.Clamp(seconds, 1, 60);
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        var view = new ViewResult
        {
            ViewName = "~/Views/Account/TooManyAttempts.cshtml",
            StatusCode = StatusCodes.Status429TooManyRequests,
            ViewData = new ViewDataDictionary<int>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = seconds },
        };
        var executor = http.RequestServices.GetRequiredService<IActionResultExecutor<ViewResult>>();
        await executor.ExecuteAsync(new ActionContext(http, http.GetRouteData(), new ActionDescriptor()), view);
    }
}
