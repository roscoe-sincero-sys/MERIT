using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

public sealed record UsagePage(int Days, ScreenUsageReport Report);
public sealed record UsageSessionPage(int Days, int Page, Guid Reference, ScreenSessionReport Report);

[Route("admin/usage")]
[Authorize(Policy = Permissions.UsageView)]
public sealed class UsageController(ScreenUsageService usage, AuditService audit) : Controller
{
    [HttpGet("lookup")]
    public IActionResult Lookup(Guid reference, int days = 30)
    {
        if (!ModelState.IsValid || reference == Guid.Empty)
            throw new Merit.Application.Errors.ValidationException("reference", "Enter a valid session reference from a usage report.");
        return RedirectToAction(nameof(Session), new { reference, days = Period(days) });
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(int days = 30, CancellationToken ct = default)
        => View(new UsagePage(Period(days), await usage.ReportAsync(Period(days), ct)));

    [HttpGet("summary.csv")]
    public async Task<IActionResult> Csv(int days = 30, CancellationToken ct = default)
    {
        var report = await usage.ReportAsync(Period(days), ct);
        var rows = report.Screens.Select(s => (IReadOnlyList<object?>)new object?[] {
            "screen", ScreenCatalog.Name(s.ScreenCode), ScreenCatalog.Route(s.ScreenCode), s.RoleCode,
            s.Visits, s.Sessions, s.TimedVisits, s.AverageVisibleMs, null }).ToList();
        rows.AddRange(report.Paths.Select(p => (IReadOnlyList<object?>)new object?[] {
            p.Kind, ScreenCatalog.PathName(p.PathCodes), null, null, p.Occurrences, p.Sessions, null, null, null }));
        rows.Add(["search-to-saved", "Search → person → enter result → saved", null, null,
            report.Flow.Completions, null, null, null, report.Flow.AverageElapsedSeconds]);
        await audit.ExportedAsync("screenUsage", null, rows.Count, ct);
        return File(TabularExport.Csv(["Kind", "Screen or path", "Normalized route", "Role (blank = all)", "Visits or occurrences",
            "Sessions", "Timed visits", "Average visible milliseconds", "Average elapsed flow seconds"], rows), TabularExport.CsvType, "merit-usage-summary.csv");
    }

    [HttpGet("sessions/{reference:guid}")]
    public async Task<IActionResult> Session(Guid reference, int days = 30, int page = 1, CancellationToken ct = default)
    {
        var result = await usage.SessionAsync(reference, Period(days), Page(page), ct);
        return View(new UsageSessionPage(days, page, reference, result));
    }

    [HttpGet("sessions/{reference:guid}/page.csv")]
    public async Task<IActionResult> SessionCsv(Guid reference, int days = 30, int page = 1, CancellationToken ct = default)
    {
        var result = await usage.SessionAsync(reference, Period(days), Page(page), ct);
        await audit.ExportedAsync("screenUsage", null, result.Visits.Count, ct);
        return File(TabularExport.Csv(["When UTC", "Screen", "Normalized route", "Navigation hint", "Tab", "Roles", "Visible milliseconds",
            "Visit", "Previous visit", "Working tenant", "Profile", "View-as user"],
            result.Visits.Select(v => (IReadOnlyList<object?>)new object?[] { v.EventUtc, ScreenCatalog.Name(v.ScreenCode), ScreenCatalog.Route(v.ScreenCode),
                v.NavigationType, v.TabId, v.RoleCodes, v.DurationMs, v.ScreenVisitId, v.PreviousVisitId, v.ActingTenantId, v.ProfileId, v.ViewAsUserId })),
            TabularExport.CsvType, $"merit-session-page-{page}.csv");
    }

    private static int Period(int days) => days is >= 1 and <= 90 ? days
        : throw new Merit.Application.Errors.ValidationException("days", "Choose a period from 1 to 90 days.");
    private static int Page(int page) => page is >= 1 and <= 100000 ? page
        : throw new Merit.Application.Errors.ValidationException("page", "Choose a valid page.");
}
