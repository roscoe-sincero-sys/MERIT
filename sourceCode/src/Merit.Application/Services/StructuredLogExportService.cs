using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Security;

namespace Merit.Application.Services;

/// <summary>
/// Deterministic keyed pseudonymizer for AI-agent-safe structured log exports (#328).
/// Preserves relational grouping (same user or same IP prefix produces the same pseudonym within one export)
/// while removing workforce names, usernames, full IPs, and free-text search terms.
/// </summary>
public sealed class LogPseudonymizer
{
    private readonly byte[] key;

    public LogPseudonymizer(byte[]? key = null)
        => this.key = key is { Length: >= 16 } ? key : RandomNumberGenerator.GetBytes(32);

    public string? PseudonymizeUser(Guid? userId)
        => userId is Guid id ? "usr_" + HashHex("user:" + id.ToString("N"))[..16] : null;

    public string? PseudonymizeIp(string? clientAddress)
    {
        if (string.IsNullOrWhiteSpace(clientAddress)) return null;
        string trimmed = clientAddress.Trim();
        string prefix = MaskPrefix(trimmed);
        return $"{prefix}|ip_{HashHex("ip:" + trimmed)[..12]}";
    }

    private string HashHex(string input)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static string MaskPrefix(string ipText)
    {
        if (!IPAddress.TryParse(ipText, out var ip)) return "ip";
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        if (bytes.Length == 16) return $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}::/48";
        return "ip";
    }
}

/// <summary>
/// Unified, AI-agent-ready structured log export (JSONL and canonical lossless CSV) with cursor-based pagination,
/// ISO-8601 UTC timestamps, canonical codes, correlation keys, and optional PII pseudonymization (#328).
/// </summary>
public sealed class StructuredLogExportService(ISecurityMonitoringData data, AuditService audit, ICurrentSession session)
{
    public const string IsoUtcFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static string FormatUtc(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(IsoUtcFormat, CultureInfo.InvariantCulture);

    public static string? FormatUtc(DateTime? utc)
        => utc is DateTime d ? FormatUtc(d) : null;

    /// <summary>
    /// Streams canonical JSONL (NDJSON) records across the requested streams, cursor-paged without hard truncation.
    /// Audited as an <c>export</c> event with total exported row count.
    /// </summary>
    public async Task<int> WriteJsonlAsync(Stream output, DateTime fromUtc, DateTime toUtc, IReadOnlySet<string> streams,
                                           bool pseudonymize, byte[]? pseudonymKey = null, int batchSize = 2000,
                                           CancellationToken ct = default)
    {
        var pseudo = pseudonymize ? new LogPseudonymizer(pseudonymKey) : null;
        int total = 0;
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 16384, leaveOpen: true);

        if (streams.Contains("audit"))
        {
            session.Require(Permissions.AuditView);
            DateTime? cursorUtc = null;
            long? cursorId = null;
            while (true)
            {
                var batch = await data.ExportAuditEventsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        stream = "auditEvent",
                        auditEventId = r.AuditEventId,
                        eventUtc = FormatUtc(r.EventUtc),
                        eventAction = r.EventAction,
                        entityType = r.EntityType,
                        entityId = r.EntityId,
                        recordCount = r.RecordCount,
                        viewKind = r.ViewKind,
                        appUserId = pseudo is not null ? (object?)pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId,
                        userName = pseudo is not null ? null : r.UserName,
                        displayName = pseudo is not null ? null : r.DisplayName,
                        rootTenantId = r.RootTenantId,
                        owningTenantId = r.OwningTenantId,
                        actingTenantId = r.ActingTenantId,
                        viewAsUserId = pseudo is not null ? (object?)pseudo.PseudonymizeUser(r.ViewAsUserId) : r.ViewAsUserId,
                        sessionReference = r.SessionReference,
                        traceId = r.TraceId,
                        clientAddress = pseudo is not null ? pseudo.PseudonymizeIp(r.ClientAddress) : r.ClientAddress,
                        filterJson = pseudonymize ? null : r.FilterJson,
                    })).ConfigureAwait(false);
                    total++;
                }

                var last = batch[^1];
                cursorUtc = last.EventUtc;
                cursorId = last.AuditEventId;
                if (batch.Count < batchSize) break;
            }
        }

        if (streams.Contains("signin"))
        {
            session.RequireAny(Permissions.SecurityReview, Permissions.TenantAdminister);
            DateTime? cursorUtc = null;
            Guid? cursorId = null;
            while (true)
            {
                var batch = await data.ExportSignInEventsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        stream = "signInEvent",
                        signInEventId = r.SignInEventId,
                        eventUtc = FormatUtc(r.EventUtc),
                        eventType = r.EventType,
                        step = r.Step,
                        method = r.Method,
                        reason = r.Reason,
                        appUserId = pseudo is not null ? (object?)pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId,
                        userName = pseudo is not null ? null : r.UserName,
                        displayName = pseudo is not null ? null : r.DisplayName,
                        actorUserId = pseudo is not null ? (object?)pseudo.PseudonymizeUser(r.ActorUserId) : r.ActorUserId,
                        rootTenantId = r.RootTenantId,
                        clientAddress = pseudo is not null ? pseudo.PseudonymizeIp(r.ClientAddress) : r.ClientAddress,
                        userAgent = r.UserAgent,
                        sessionReference = r.SessionReference,
                        attemptId = r.AttemptId,
                        traceId = r.TraceId,
                        failedSignInCount = r.FailedSignInCount,
                        lockoutEndUtc = FormatUtc(r.LockoutEndUtc),
                    })).ConfigureAwait(false);
                    total++;
                }

                var last = batch[^1];
                cursorUtc = last.EventUtc;
                cursorId = last.SignInEventId;
                if (batch.Count < batchSize) break;
            }
        }

        if (streams.Contains("usage"))
        {
            session.Require(Permissions.UsageView);
            DateTime? cursorUtc = null;
            Guid? cursorId = null;
            while (true)
            {
                var batch = await data.ExportScreenVisitsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        stream = "screenVisit",
                        screenVisitId = r.ScreenVisitId,
                        eventUtc = FormatUtc(r.EventUtc),
                        rootTenantId = r.RootTenantId,
                        actingTenantId = r.ActingTenantId,
                        appUserId = pseudo is not null ? (object?)pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId,
                        userName = pseudo is not null ? null : r.UserName,
                        displayName = pseudo is not null ? null : r.DisplayName,
                        sessionReference = r.SessionReference,
                        profileId = r.ProfileId,
                        viewAsUserId = pseudo is not null ? (object?)pseudo.PseudonymizeUser(r.ViewAsUserId) : r.ViewAsUserId,
                        roleCodes = r.RoleCodes,
                        screenCode = r.ScreenCode,
                        navigationType = r.NavigationType,
                        tabId = r.TabId,
                        previousVisitId = r.PreviousVisitId,
                        durationMs = r.DurationMs,
                    })).ConfigureAwait(false);
                    total++;
                }

                var last = batch[^1];
                cursorUtc = last.EventUtc;
                cursorId = last.ScreenVisitId;
                if (batch.Count < batchSize) break;
            }
        }

        if (streams.Contains("search"))
        {
            session.RequireAny(Permissions.SecurityReview, Permissions.AuditView);
            DateTime? cursorUtc = null;
            Guid? cursorId = null;
            while (true)
            {
                var batch = await data.ExportSearchLogsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        stream = "searchLog",
                        searchLogId = r.SearchLogId,
                        searchedUtc = FormatUtc(r.SearchedUtc),
                        appUserId = pseudo is not null ? (object?)pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId,
                        userName = pseudo is not null ? null : r.UserName,
                        displayName = pseudo is not null ? null : r.DisplayName,
                        rootTenantId = r.RootTenantId,
                        actingTenantId = r.ActingTenantId,
                        sessionReference = r.SessionReference,
                        clientAddress = pseudo is not null ? pseudo.PseudonymizeIp(r.ClientAddress) : r.ClientAddress,
                        resultCount = r.ResultCount,
                        existsElsewhere = r.ExistsElsewhere,
                        searchSource = r.SearchSource,
                    })).ConfigureAwait(false);
                    total++;
                }

                var last = batch[^1];
                cursorUtc = last.SearchedUtc;
                cursorId = last.SearchLogId;
                if (batch.Count < batchSize) break;
            }
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        await audit.ExportedAsync("structuredLogExport", null, total, ct,
            new { format = "jsonl", streams = streams.OrderBy(s => s).ToArray(), pseudonymize, fromUtc = FormatUtc(fromUtc), toUtc = FormatUtc(toUtc) }).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    /// Streams canonical lossless CSV across the requested streams using unified envelope columns and cursor pagination.
    /// </summary>
    public async Task<int> WriteCsvAsync(Stream output, DateTime fromUtc, DateTime toUtc, IReadOnlySet<string> streams,
                                         bool pseudonymize, byte[]? pseudonymKey = null, int batchSize = 2000,
                                         CancellationToken ct = default)
    {
        var pseudo = pseudonymize ? new LogPseudonymizer(pseudonymKey) : null;
        int total = 0;
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 16384, leaveOpen: true);
        await writer.WriteLineAsync("stream,eventId,eventUtc,actionOrType,entityOrScreen,entityId,recordCount,appUserId,userName,rootTenantId,owningTenantId,actingTenantId,sessionReference,traceId,attemptId,clientAddress,detail").ConfigureAwait(false);

        static string Csv(object? value)
        {
            if (value is null) return string.Empty;
            string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) s = "'" + s;
            return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        Task WriteRow(params object?[] cols) => writer.WriteLineAsync(string.Join(",", cols.Select(Csv)));

        if (streams.Contains("audit"))
        {
            session.Require(Permissions.AuditView);
            DateTime? cursorUtc = null;
            long? cursorId = null;
            while (true)
            {
                var batch = await data.ExportAuditEventsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await WriteRow("auditEvent", r.AuditEventId, FormatUtc(r.EventUtc), r.EventAction, r.EntityType, r.EntityId, r.RecordCount,
                        pseudo is not null ? pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId?.ToString(),
                        pseudo is not null ? null : r.UserName,
                        r.RootTenantId, r.OwningTenantId, r.ActingTenantId, r.SessionReference, r.TraceId, null,
                        pseudo is not null ? pseudo.PseudonymizeIp(r.ClientAddress) : r.ClientAddress,
                        r.ViewKind).ConfigureAwait(false);
                    total++;
                }

                cursorUtc = batch[^1].EventUtc;
                cursorId = batch[^1].AuditEventId;
                if (batch.Count < batchSize) break;
            }
        }

        if (streams.Contains("signin"))
        {
            session.RequireAny(Permissions.SecurityReview, Permissions.TenantAdminister);
            DateTime? cursorUtc = null;
            Guid? cursorId = null;
            while (true)
            {
                var batch = await data.ExportSignInEventsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await WriteRow("signInEvent", r.SignInEventId, FormatUtc(r.EventUtc), r.EventType, r.Method ?? r.Step, null, r.FailedSignInCount,
                        pseudo is not null ? pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId?.ToString(),
                        pseudo is not null ? null : r.UserName,
                        r.RootTenantId, null, null, r.SessionReference, r.TraceId, r.AttemptId,
                        pseudo is not null ? pseudo.PseudonymizeIp(r.ClientAddress) : r.ClientAddress,
                        r.Reason).ConfigureAwait(false);
                    total++;
                }

                cursorUtc = batch[^1].EventUtc;
                cursorId = batch[^1].SignInEventId;
                if (batch.Count < batchSize) break;
            }
        }

        if (streams.Contains("usage"))
        {
            session.Require(Permissions.UsageView);
            DateTime? cursorUtc = null;
            Guid? cursorId = null;
            while (true)
            {
                var batch = await data.ExportScreenVisitsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await WriteRow("screenVisit", r.ScreenVisitId, FormatUtc(r.EventUtc), r.NavigationType, r.ScreenCode, r.PreviousVisitId, r.DurationMs,
                        pseudo is not null ? pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId?.ToString(),
                        pseudo is not null ? null : r.UserName,
                        r.RootTenantId, null, r.ActingTenantId, r.SessionReference, null, null, null, r.RoleCodes).ConfigureAwait(false);
                    total++;
                }

                cursorUtc = batch[^1].EventUtc;
                cursorId = batch[^1].ScreenVisitId;
                if (batch.Count < batchSize) break;
            }
        }

        if (streams.Contains("search"))
        {
            session.RequireAny(Permissions.SecurityReview, Permissions.AuditView);
            DateTime? cursorUtc = null;
            Guid? cursorId = null;
            while (true)
            {
                var batch = await data.ExportSearchLogsAsync(fromUtc, toUtc, cursorUtc, cursorId, batchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    await WriteRow("searchLog", r.SearchLogId, FormatUtc(r.SearchedUtc), r.SearchSource ?? "global", "searchLog", null, r.ResultCount,
                        pseudo is not null ? pseudo.PseudonymizeUser(r.AppUserId) : r.AppUserId.ToString(),
                        pseudo is not null ? null : r.UserName,
                        r.RootTenantId, null, r.ActingTenantId, r.SessionReference, null, null,
                        pseudo is not null ? pseudo.PseudonymizeIp(r.ClientAddress) : r.ClientAddress,
                        r.ExistsElsewhere == true ? "existsElsewhere" : null).ConfigureAwait(false);
                    total++;
                }

                cursorUtc = batch[^1].SearchedUtc;
                cursorId = batch[^1].SearchLogId;
                if (batch.Count < batchSize) break;
            }
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        await audit.ExportedAsync("structuredLogExport", null, total, ct,
            new { format = "csv", streams = streams.OrderBy(s => s).ToArray(), pseudonymize, fromUtc = FormatUtc(fromUtc), toUtc = FormatUtc(toUtc) }).ConfigureAwait(false);
        return total;
    }
}
