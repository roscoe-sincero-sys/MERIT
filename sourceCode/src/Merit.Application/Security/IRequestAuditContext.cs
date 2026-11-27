using System.Security.Cryptography;
using System.Text;

namespace Merit.Application.Security;

/// <summary>
/// Per-request network and trace context for auditing and telemetry (#325, #326). The web host populates this from
/// HttpContext (after trusted forwarded headers) using the W3C trace id; the worker and tests fall back to
/// <see cref="NullRequestAuditContext"/>.
/// </summary>
public interface IRequestAuditContext
{
    string? ClientAddress { get; }

    /// <summary>Canonical W3C trace id (Activity.Current.TraceId), falling back to HttpContext.TraceIdentifier.</summary>
    string? TraceId { get; }

    string? UserAgent { get; }

    /// <summary>First 16 uppercase hex chars of keyed HMAC-SHA256 over the normalized User-Agent family (#327, #350).</summary>
    string? UserAgentHash => null;

    /// <summary>Normalized route pattern (never raw query string or patient values).</summary>
    string? RouteTemplate { get; }

    /// <summary>
    /// Normalizes a raw User-Agent into a version-free client family (<c>client|platform|device</c>) so browser
    /// background updates do not trigger Rule 11 (<c>midSessionUserAgentDrift</c>) while real client switches do (#350).
    /// </summary>
    static string? NormalizeUserAgentFamily(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        string ua = userAgent.Trim().ToLowerInvariant();

        // Automated / CLI / headless clients first (keep distinct from interactive browsers).
        if (ua.Contains("headlesschrome")) return "bot:headlesschrome|other|cli";
        if (ua.Contains("playwright")) return "bot:playwright|other|cli";
        if (ua.Contains("puppeteer")) return "bot:puppeteer|other|cli";
        if (ua.Contains("selenium")) return "bot:selenium|other|cli";
        if (ua.Contains("curl/")) return "bot:curl|other|cli";
        if (ua.Contains("python-requests")) return "bot:python-requests|other|cli";
        if (ua.Contains("python-urllib") || ua.Contains("python/")) return "bot:python|other|cli";
        if (ua.Contains("powershell")) return "bot:powershell|other|cli";
        if (ua.Contains("wget/")) return "bot:wget|other|cli";
        if (ua.Contains("go-http-client")) return "bot:go-http|other|cli";
        if (ua.Contains("httpie")) return "bot:httpie|other|cli";

        string browser =
            ua.Contains("edg/") || ua.Contains("edga/") || ua.Contains("edgios/") || ua.Contains("edge/") ? "edge" :
            ua.Contains("opr/") || ua.Contains("opera") ? "opera" :
            ua.Contains("firefox/") || ua.Contains("fxios/") ? "firefox" :
            ua.Contains("chrome/") || ua.Contains("crios/") ? "chrome" :
            ua.Contains("safari/") ? "safari" :
            ua.Contains("trident/") || ua.Contains("msie") ? "ie" :
            "other";

        string platform =
            ua.Contains("iphone") || ua.Contains("ipad") || ua.Contains("ipod") ? "ios" :
            ua.Contains("android") ? "android" :
            ua.Contains("windows") ? "windows" :
            ua.Contains("cros") ? "cros" :
            ua.Contains("mac os x") || ua.Contains("macintosh") ? "macos" :
            ua.Contains("linux") ? "linux" :
            "other";

        string device =
            ua.Contains("ipad") || ua.Contains("tablet") ? "tablet" :
            ua.Contains("mobile") || ua.Contains("iphone") || ua.Contains("ipod") ? "mobile" :
            "desktop";

        return $"{browser}|{platform}|{device}";
    }

    /// <summary>Computes the 64-bit keyed HMAC-SHA256 hex prefix over the normalized User-Agent family (#350).</summary>
    static string? ComputeKeyedUserAgentHash(string? userAgent, ReadOnlySpan<byte> key)
    {
        string? family = NormalizeUserAgentFamily(userAgent);
        if (family is null) return null;
        byte[] hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(family));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}

/// <summary>Keyed User-Agent family hasher backed by <c>Merit:UserAgentHashKey</c> (#350).</summary>
public interface IUserAgentHasher
{
    string? Hash(string? userAgent);
}

public sealed class NullRequestAuditContext : IRequestAuditContext
{
    public static readonly NullRequestAuditContext Instance = new();

    public string? ClientAddress => null;

    public string? TraceId => System.Diagnostics.Activity.Current?.TraceId.ToString();

    public string? UserAgent => null;

    public string? RouteTemplate => null;
}

/// <summary>One record identifier in a list/export manifest (#333).</summary>
public sealed record AuditManifestEntry(string EntityType, Guid EntityId);

/// <summary>Row returned by logs.uspGetAuditEventRecords (#333).</summary>
public sealed class AuditEventRecordRow
{
    public string EntityType { get; init; } = string.Empty;

    public Guid EntityId { get; init; }
}

/// <summary>Scoped manifest rows plus count of rows outside the caller's review scope (#341).</summary>
public sealed record AuditEventRecordsResult(IReadOnlyList<AuditEventRecordRow> Records, int HiddenCount);
