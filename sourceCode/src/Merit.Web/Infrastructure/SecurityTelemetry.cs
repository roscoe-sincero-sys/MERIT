using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Merit.Application.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Canonical request trace identifier (#326): prefers the W3C Activity trace id (carried by OpenTelemetry and
/// SignInEventRecorder) and falls back to HttpContext.TraceIdentifier when no Activity is active.
/// </summary>
public static class RequestTrace
{
    public static string? Id(HttpContext? http)
        => Activity.Current?.TraceId.ToString() is { Length: > 0 } w3c ? w3c : http?.TraceIdentifier;

    public static string? RouteTemplate(HttpContext? http)
    {
        if (http is null) return null;
        if (http.GetEndpoint() is RouteEndpoint re && !string.IsNullOrWhiteSpace(re.RoutePattern.RawText))
        {
            return "/" + re.RoutePattern.RawText.TrimStart('/');
        }

        // Normalize unmatched paths by stripping query string and capping length (never log query params or PHI).
        string path = http.Request.Path.Value ?? "/";
        return path.Length <= 120 ? path : path[..120];
    }

    public static string? SanitizeUserAgent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Length <= 400 ? raw : raw[..400];
        return trimmed.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    public static string EntityTypeFromRoute(HttpContext? http)
    {
        string? template = RouteTemplate(http)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(template)) return "route";
        if (template.Contains("/people")) return "person";
        if (template.Contains("/results")) return "labResult";
        if (template.Contains("/cases")) return "exposureCase";
        if (template.Contains("/investigations")) return "investigation";
        if (template.Contains("/clusters")) return "cluster";
        if (template.Contains("/held") || template.Contains("/match-review") || template.Contains("/intake")) return "stagingRow";
        if (template.Contains("/extracts")) return "extractRun";
        if (template.Contains("/reports")) return "report";
        if (template.Contains("/admin/audit")) return "auditEvent";
        if (template.Contains("/admin/sign-in-activity")) return "signInActivity";
        if (template.Contains("/admin/usage")) return "screenUsage";
        if (template.Contains("/admin/users")) return "appUser";
        if (template.Contains("/admin/tenants")) return "tenant";
        if (template.Contains("/config/")) return "jurisdictionSetting";
        if (template.Contains("/account/profiles")) return "userProfile";
        return "route";
    }

    public static Guid? EntityIdFromRoute(HttpContext? http)
    {
        if (http is null) return null;
        foreach (string key in new[] { "id", "personId", "exposureCaseId", "labResultId", "investigationId", "stagingRowId" })
        {
            if (http.Request.RouteValues.TryGetValue(key, out object? value)
                && Guid.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out Guid id))
            {
                return id;
            }
        }

        return null;
    }
}

/// <summary>Per-request audit context populated from HttpContext (#326).</summary>
public sealed class WebRequestAuditContext(IHttpContextAccessor accessor) : IRequestAuditContext
{
    public string? ClientAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? TraceId => RequestTrace.Id(accessor.HttpContext);

    public string? UserAgent => RequestTrace.SanitizeUserAgent(accessor.HttpContext?.Request.Headers.UserAgent.ToString());

    public string? RouteTemplate => RequestTrace.RouteTemplate(accessor.HttpContext);
}

/// <summary>
/// Flags MVC antiforgery validation failures (returned by AutoValidateAntiforgeryTokenAttribute as
/// IAntiforgeryValidationFailedResult) so <see cref="SecurityTelemetryMiddleware"/> logs them (#325).
/// </summary>
public sealed class AntiforgeryTelemetryFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is IAntiforgeryValidationFailedResult)
        {
            context.HttpContext.Items[SecurityTelemetryMiddleware.AntiforgeryFailedItem] = true;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}

/// <summary>
/// Emits structured, non-PHI security telemetry for HTTP 401, 403 (including browser 302 access-denied redirects),
/// 404 (non-static), 405, 422, 429 and antiforgery validation failures (#325). Skips StatusCodePages re-executions so
/// each failed request is logged once. Never logs query parameter values, form bodies, patient data or credentials.
/// </summary>
public sealed partial class SecurityTelemetryMiddleware(RequestDelegate next, ILogger<SecurityTelemetryMiddleware> logger)
{
    public const string AccessDeniedItem = "merit.telemetry.accessDenied";
    public const string AntiforgeryFailedItem = "merit.telemetry.antiforgeryFailed";

    private static readonly string[] StaticExtensions = [".js", ".css", ".png", ".jpg", ".svg", ".ico", ".woff", ".woff2", ".map"];

    public async Task InvokeAsync(HttpContext http)
    {
        long started = Stopwatch.GetTimestamp();
        await next(http).ConfigureAwait(false);

        // Skip re-executed /home/status/{code} passes so each failed request is logged once with its original path.
        if (http.Features.Get<IStatusCodeReExecuteFeature>() is not null)
        {
            return;
        }

        int status = http.Response.StatusCode;
        bool accessDenied = http.Items.ContainsKey(AccessDeniedItem);
        bool antiforgeryFailed = http.Items.ContainsKey(AntiforgeryFailedItem)
                                 || http.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false };
        if (!antiforgeryFailed && !accessDenied && status is not (401 or 403 or 404 or 405 or 422 or 429))
        {
            return;
        }

        string path = http.Request.Path.Value ?? "/";
        if (status == 404 && !accessDenied && !antiforgeryFailed
            && StaticExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        int reportedStatus = accessDenied ? StatusCodes.Status403Forbidden : status;
        string category = antiforgeryFailed ? "antiforgery" : accessDenied ? "forbidden" : status switch
        {
            401 => "unauthorized",
            403 => "forbidden",
            404 => "notFound",
            405 => "methodNotAllowed",
            422 => "validation",
            429 => "rateLimited",
            _ => "security",
        };

        Guid? appUserId = Guid.TryParse(http.User.FindFirstValue(MeritClaims.UserId), CultureInfo.InvariantCulture, out Guid uid) ? uid : null;
        string? route = RequestTrace.RouteTemplate(http);
        string? traceId = RequestTrace.Id(http);
        string? address = http.Connection.RemoteIpAddress?.ToString();
        string? ua = RequestTrace.SanitizeUserAgent(http.Request.Headers.UserAgent.ToString());
        long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        SecurityHttpEvent(logger, category, reportedStatus, http.Request.Method, route, appUserId, address, ua, traceId, elapsedMs);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Security HTTP {Category} status {StatusCode} {Method} {RouteTemplate} user {AppUserId} address {ClientAddress} agent {UserAgent} trace {TraceId} elapsed {ElapsedMs}ms")]
    private static partial void SecurityHttpEvent(ILogger logger, string category, int statusCode, string method, string? routeTemplate,
                                                  Guid? appUserId, string? clientAddress, string? userAgent, string? traceId, long elapsedMs);
}
