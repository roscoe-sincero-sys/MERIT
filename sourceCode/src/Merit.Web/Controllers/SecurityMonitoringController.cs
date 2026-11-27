using System.Text;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

public sealed record SecurityAlertsPage(
    int Hours,
    int Days,
    string? Status,
    bool CanEditFindings,
    IReadOnlyList<BehaviorAnomalyRow> Anomalies,
    IReadOnlyList<SecurityFindingRow> Findings,
    IReadOnlyList<SearchLogSummaryRow> SearchSummary);

public sealed record AuditChangesPage(long AuditEventId, IReadOnlyList<AuditEventChangeRow> Changes);

public sealed record LogExportPage(
    DateOnly FromDate,
    DateOnly ToDate,
    bool CanAudit,
    bool CanSecurity,
    bool CanUsage);

public sealed record UserActivityPage(int Days, UserBehaviorSummary Summary);

public sealed record OperationsLogPage(int Hours, OperationalLogsReport Report);

/// <summary>
/// Administration screens and exports for behavioral anomaly detection (#327), unified structured log export (#328),
/// search-log reporting (#329), per-user behavior summaries &amp; operational logs (#330).
/// </summary>
[Route("admin")]
public sealed class SecurityMonitoringController(
    SecurityMonitoringService monitoring,
    StructuredLogExportService exporter,
    AuditService audit,
    ICurrentSession session,
    IClock clock) : Controller
{
    private static string Iso(DateTime d) => StructuredLogExportService.FormatUtc(d);

    // --------------------------------------------------------------------------------------------- #327 Security alerts
    [HttpGet("security-alerts")]
    [Authorize(Policy = Permissions.SecurityReports)]
    public async Task<IActionResult> Alerts(int hours = 24, int days = 30, string? status = null, CancellationToken ct = default)
    {
        int h = Math.Clamp(hours, 1, 2160);
        int d = Math.Clamp(days, 1, 365);
        var anomalies = await monitoring.GetBehaviorAnomaliesAsync(new BehaviorAnomalyQuery(Hours: h), ct);
        var findings = await monitoring.ListFindingsAsync(d, status, null, null, ct);
        var searchSummary = await monitoring.GetSearchLogSummaryAsync(d, null, ct);
        bool canEdit = session.Has(Permissions.TenantAdminister) || session.Has(Permissions.SecurityInvestigate);
        return View(new SecurityAlertsPage(h, d, status, canEdit, anomalies, findings, searchSummary));
    }

    [HttpPost("security-alerts/{id:guid}/status")]
    [Authorize(Policy = Permissions.SecurityManage)]
    public async Task<IActionResult> UpdateFindingStatus(Guid id, string status, int hours = 24, int days = 30, CancellationToken ct = default)
    {
        await monitoring.UpdateFindingStatusAsync(id, status, ct);
        return RedirectToAction(nameof(Alerts), new { hours, days });
    }

    [HttpGet("audit/{id:long}/changes")]
    [Authorize(Policy = Permissions.AuditView)]
    public async Task<IActionResult> AuditChanges(long id, CancellationToken ct = default)
        => View(new AuditChangesPage(id, await monitoring.GetAuditEventChangesAsync(id, ct)));

    [HttpGet("security-alerts.csv")]
    [Authorize(Policy = Permissions.SecurityReports)]
    public async Task<IActionResult> AlertsCsv(int hours = 24, int days = 30, string? status = null, CancellationToken ct = default)
    {
        int h = Math.Clamp(hours, 1, 2160);
        int d = Math.Clamp(days, 1, 365);
        var anomalies = await monitoring.GetBehaviorAnomaliesAsync(new BehaviorAnomalyQuery(Hours: h), ct, auditView: false);
        var findings = await monitoring.ListFindingsAsync(d, status, null, null, ct, auditView: false);
        var rows = new List<IReadOnlyList<object?>>();
        rows.AddRange(anomalies.Select(a => (IReadOnlyList<object?>)[
            "liveAnomaly", a.RuleCode, a.Severity, "detected", a.AppUserId, a.UserName, a.ClientAddress,
            a.SessionReference, a.EventCount, a.Title, a.DetailJson, Iso(a.FirstUtc), Iso(a.LastUtc)]));
        rows.AddRange(findings.Select(f => (IReadOnlyList<object?>)[
            "persistedFinding", f.RuleCode, f.Severity, f.Status, f.AppUserId, f.UserName, f.ClientAddress,
            f.SessionReference, null, f.Title, f.DetailJson, Iso(f.WindowStartUtc), Iso(f.DetectedUtc)]));
        await audit.ExportedAsync("securityAlert", null, rows.Count, ct, new { format = "csv", hours = h, days = d });
        return File(TabularExport.Csv(
            ["Kind", "RuleCode", "Severity", "Status", "AppUserId", "UserName", "ClientAddress", "SessionReference", "EventCount", "Title", "DetailJson", "FirstUtc", "LastUtc"],
            rows), TabularExport.CsvType, $"merit-security-alerts-{clock.Today:yyyy-MM-dd}.csv");
    }

    [HttpGet("security-alerts.jsonl")]
    [Authorize(Policy = Permissions.SecurityReports)]
    public async Task<IActionResult> AlertsJsonl(int hours = 24, int days = 30, string? status = null, CancellationToken ct = default)
    {
        int h = Math.Clamp(hours, 1, 2160);
        int d = Math.Clamp(days, 1, 365);
        var anomalies = await monitoring.GetBehaviorAnomaliesAsync(new BehaviorAnomalyQuery(Hours: h), ct, auditView: false);
        var findings = await monitoring.ListFindingsAsync(d, status, null, null, ct, auditView: false);
        var sb = new StringBuilder();
        foreach (var a in anomalies)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                kind = "liveAnomaly",
                ruleCode = a.RuleCode,
                severity = a.Severity,
                appUserId = a.AppUserId,
                userName = a.UserName,
                clientAddress = a.ClientAddress,
                sessionReference = a.SessionReference,
                eventCount = a.EventCount,
                title = a.Title,
                detailJson = a.DetailJson,
                firstUtc = Iso(a.FirstUtc),
                lastUtc = Iso(a.LastUtc),
            }));
        }

        foreach (var f in findings)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                kind = "persistedFinding",
                securityFindingId = f.SecurityFindingId,
                ruleCode = f.RuleCode,
                severity = f.Severity,
                status = f.Status,
                appUserId = f.AppUserId,
                userName = f.UserName,
                clientAddress = f.ClientAddress,
                sessionReference = f.SessionReference,
                title = f.Title,
                detailJson = f.DetailJson,
                windowStartUtc = Iso(f.WindowStartUtc),
                windowEndUtc = Iso(f.WindowEndUtc),
                detectedUtc = Iso(f.DetectedUtc),
            }));
        }

        await audit.ExportedAsync("securityAlert", null, anomalies.Count + findings.Count, ct, new { format = "jsonl", hours = h, days = d });
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-ndjson", $"merit-security-alerts-{clock.Today:yyyy-MM-dd}.jsonl");
    }

    // --------------------------------------------------------------------------------------------- #328 Structured export
    [HttpGet("log-export")]
    [Authorize(Policy = Permissions.LogReview)]
    public IActionResult LogExport()
    {
        bool canAudit = session.Has(Permissions.AuditView);
        bool canSecurity = session.Has(Permissions.SecurityReview) || session.Has(Permissions.TenantAdminister);
        bool canUsage = session.Has(Permissions.UsageView);
        if (!canAudit && !canSecurity && !canUsage) return Forbid();
        var today = clock.Today;
        return View(new LogExportPage(today.AddDays(-7), today, canAudit, canSecurity, canUsage));
    }

    [HttpGet("log-export/download")]
    [Authorize(Policy = Permissions.LogReview)]
    public async Task<IActionResult> DownloadLogExport(
        string format = "jsonl",
        string streams = "audit",
        DateOnly? from = null,
        DateOnly? to = null,
        bool pseudonymize = true,
        CancellationToken ct = default)
    {
        var today = clock.Today;
        var fromDate = from ?? today.AddDays(-7);
        var toDate = to ?? today;
        if (toDate < fromDate) (fromDate, toDate) = (toDate, fromDate);
        if (toDate.DayNumber - fromDate.DayNumber > 31)
        {
            throw new ValidationException("from", "Export range cannot exceed 31 days per request. Download longer periods in monthly chunks.");
        }

        // Half-open UTC range [fromUtc, toUtc) avoids end-of-day rounding into the following midnight (Codex #2).
        DateTime fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime toUtc = toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var selected = streams.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => s is "audit" or "signin" or "usage" or "search")
            .ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) selected.Add("audit");

        // Pre-authorize every requested stream before streaming response bytes (Claude I3, Codex #7).
        exporter.AuthorizeStreams(selected);

        bool isCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        string ext = isCsv ? "csv" : "jsonl";
        string fileName = $"merit-logs-{fromDate:yyyy-MM-dd}-to-{toDate:yyyy-MM-dd}{(pseudonymize ? "-pseudonymized" : "")}.{ext}";

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = isCsv ? TabularExport.CsvType : "application/x-ndjson";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        if (isCsv)
        {
            await exporter.WriteCsvAsync(Response.Body, fromUtc, toUtc, selected, pseudonymize, ct: ct);
        }
        else
        {
            await exporter.WriteJsonlAsync(Response.Body, fromUtc, toUtc, selected, pseudonymize, ct: ct);
        }

        return new EmptyResult();
    }

    // --------------------------------------------------------------------------------------------- #330 User behavior summary
    [HttpGet("users/{id:guid}/activity")]
    [Authorize(Policy = Permissions.LogReview)]
    public async Task<IActionResult> UserActivity(Guid id, int days = 30, CancellationToken ct = default)
    {
        int d = Math.Clamp(days, 1, 365);
        var summary = await monitoring.GetUserBehaviorSummaryAsync(id, d, ct);
        return View(new UserActivityPage(d, summary));
    }

    [HttpGet("users/{id:guid}/activity.csv")]
    [Authorize(Policy = Permissions.LogReview)]
    public async Task<IActionResult> UserActivityCsv(Guid id, int days = 30, CancellationToken ct = default)
    {
        int d = Math.Clamp(days, 1, 365);
        var s = await monitoring.GetUserBehaviorSummaryAsync(id, d, ct, auditView: false);
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "overview", s.Overview.UserName, s.Overview.DisplayName, s.Overview.ActiveSessions,
                $"signIns={s.Overview.SignInSuccesses}, failures={s.Overview.SignInFailures}, lockouts={s.Overview.Lockouts}, searches={s.Overview.TotalSearches}, visits={s.Overview.ScreenVisits}",
                s.Overview.LastSignInUtc is DateTime l ? Iso(l) : null }
        };
        rows.AddRange(s.AuditActions.Select(a => (IReadOnlyList<object?>)[
            "auditAction", a.EventAction, a.EntityType, a.EventCount, $"records={a.TotalRecords}", Iso(a.LastUtc)]));
        rows.AddRange(s.Findings.Select(f => (IReadOnlyList<object?>)[
            "securityFinding", f.RuleCode, f.Severity, null, $"{f.Status}: {f.Title}", Iso(f.DetectedUtc)]));
        await audit.ExportedAsync("userBehavior", id, rows.Count, ct, new { format = "csv", days = d });
        return File(TabularExport.Csv(["Section", "Code", "Target", "Count", "Detail", "LastUtc"], rows),
            TabularExport.CsvType, $"merit-user-activity-{s.Overview.UserName}-{clock.Today:yyyy-MM-dd}.csv");
    }

    [HttpGet("users/{id:guid}/activity.jsonl")]
    [Authorize(Policy = Permissions.LogReview)]
    public async Task<IActionResult> UserActivityJsonl(Guid id, int days = 30, CancellationToken ct = default)
    {
        int d = Math.Clamp(days, 1, 365);
        var s = await monitoring.GetUserBehaviorSummaryAsync(id, d, ct, auditView: false);
        var sb = new StringBuilder();
        sb.AppendLine(JsonSerializer.Serialize(new { section = "overview", days = d, s.Overview }));
        foreach (var a in s.AuditActions) sb.AppendLine(JsonSerializer.Serialize(new { section = "auditAction", a }));
        foreach (var f in s.Findings) sb.AppendLine(JsonSerializer.Serialize(new { section = "securityFinding", f }));
        foreach (var u in s.UsageSessions) sb.AppendLine(JsonSerializer.Serialize(new { section = "usageSession", u }));
        await audit.ExportedAsync("userBehavior", id, 1 + s.AuditActions.Count + s.Findings.Count + s.UsageSessions.Count, ct, new { format = "jsonl", days = d });
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-ndjson",
            $"merit-user-activity-{s.Overview.UserName}-{clock.Today:yyyy-MM-dd}.jsonl");
    }

    // --------------------------------------------------------------------------------------------- #330 Operational logs
    [HttpGet("operations-log")]
    [Authorize(Policy = Permissions.SecurityInvestigate)]
    public async Task<IActionResult> OperationsLog(int hours = 24, CancellationToken ct = default)
    {
        int h = Math.Clamp(hours, 1, 720);
        return View(new OperationsLogPage(h, await monitoring.GetOperationalLogsAsync(h, 100, ct)));
    }

    [HttpGet("operations-log.csv")]
    [Authorize(Policy = Permissions.SecurityInvestigate)]
    public async Task<IActionResult> OperationsLogCsv(int hours = 24, CancellationToken ct = default)
    {
        int h = Math.Clamp(hours, 1, 720);
        var r = await monitoring.GetOperationalLogsAsync(h, 500, ct, auditView: false);
        var rows = new List<IReadOnlyList<object?>>();
        rows.AddRange(r.ExecutionFailures.Select(e => (IReadOnlyList<object?>)[
            "executionFailure", e.ExecutionLogId, Iso(e.StartDateUtc), e.ProcedureName, e.ErrorNumber, e.ErrorLine, e.ErrorProcedure, e.KeyParameters, e.CreatedBy]));
        rows.AddRange(r.DdlChanges.Select(d => (IReadOnlyList<object?>)[
            "ddlChange", d.ChangeId, Iso(d.EventTimeUtc), $"{d.ObjectSchema}.{d.ObjectName}", null, null, d.EventType, d.ObjectType, d.AppUser ?? d.LoginName]));
        rows.AddRange(r.IntegrityChecks.Select(c => (IReadOnlyList<object?>)[
            "ddlAuditVerify", null, null, c.CheckName, null, null, c.Status, c.Detail, null]));
        await audit.ExportedAsync("operationalLog", null, rows.Count, ct, new { format = "csv", hours = h });
        return File(TabularExport.Csv(
            ["Kind", "Id", "EventUtc", "ObjectOrCheck", "ErrorNumber", "ErrorLine", "StatusOrType", "AllowlistedDetail", "Actor"],
            rows), TabularExport.CsvType, $"merit-operations-log-{clock.Today:yyyy-MM-dd}.csv");
    }

    [HttpGet("operations-log.jsonl")]
    [Authorize(Policy = Permissions.SecurityInvestigate)]
    public async Task<IActionResult> OperationsLogJsonl(int hours = 24, CancellationToken ct = default)
    {
        int h = Math.Clamp(hours, 1, 720);
        var r = await monitoring.GetOperationalLogsAsync(h, 500, ct, auditView: false);
        var sb = new StringBuilder();
        foreach (var e in r.ExecutionFailures) sb.AppendLine(JsonSerializer.Serialize(new { kind = "executionFailure", e }));
        foreach (var d in r.DdlChanges) sb.AppendLine(JsonSerializer.Serialize(new { kind = "ddlChange", d }));
        foreach (var c in r.IntegrityChecks) sb.AppendLine(JsonSerializer.Serialize(new { kind = "ddlAuditVerify", c }));
        await audit.ExportedAsync("operationalLog", null, r.ExecutionFailures.Count + r.DdlChanges.Count + r.IntegrityChecks.Count, ct, new { format = "jsonl", hours = h });
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-ndjson", $"merit-operations-log-{clock.Today:yyyy-MM-dd}.jsonl");
    }
}
