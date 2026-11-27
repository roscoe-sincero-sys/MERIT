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

    /// <summary>First 16 uppercase hex chars of SHA-256 over the sanitized User-Agent for mid-session UA drift (#327).</summary>
    string? UserAgentHash => ComputeUserAgentHash(UserAgent);

    /// <summary>Normalized route pattern (never raw query string or patient values).</summary>
    string? RouteTemplate { get; }

    static string? ComputeUserAgentHash(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(userAgent.Trim()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
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
