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

    /// <summary>The authenticator-code steps (POST /account/mfa and /account/enroll).</summary>
    public const string Code = "signin-code";

    public const string PasswordSetting = "Merit:SignInAttemptsPerMinute";
    public const string CodeSetting = "Merit:MfaAttemptsPerMinute";

    /// <summary>Defaults sized for an office behind one address: about 30 people signing in within a minute.</summary>
    public const int DefaultPerMinute = 30;

    public static void Configure(RateLimiterOptions o)
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy(Password, http => Partition(http, PasswordSetting));
        o.AddPolicy(Code, http => Partition(http, CodeSetting));
        o.OnRejected = RejectAsync;
    }

    /// <summary>The partition key: the client address (after forwarded headers, when a proxy is configured).</summary>
    public static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> Partition(HttpContext http, string setting)
    {
        // Read at request time, so a deployment (or a test) can set the limit in configuration.
        int perMinute = Math.Max(1, http.RequestServices.GetRequiredService<IConfiguration>().GetValue(setting, DefaultPerMinute));
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
