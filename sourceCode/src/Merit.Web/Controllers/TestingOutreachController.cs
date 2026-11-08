using System.Text;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

[Authorize(Policy = Permissions.PersonView)]
[Authorize(Policy = Permissions.TaskView)]
public sealed class TestingOutreachController(CareService care, IClock clock) : Controller
{
    [HttpGet("testing")]
    public async Task<IActionResult> Index([FromQuery] TestingOutreachQuery query, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest("Check the testing filters.");
        query = query with { PlanKind = string.IsNullOrWhiteSpace(query.PlanKind) ? null : query.PlanKind };
        var result = await care.TestingOutreachAsync(query, ct);
        return View(new TestingOutreachModel(result.Rows, result.Total, result.Page, query, await care.TestingAssigneesAsync(ct)));
    }

    [HttpGet("testing.csv")]
    [Authorize(Policy = Permissions.DataExport)]
    public async Task<IActionResult> Export([FromQuery] TestingOutreachQuery query, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest("Check the testing filters.");
        query = query with { PlanKind = string.IsNullOrWhiteSpace(query.PlanKind) ? null : query.PlanKind };
        var rows = await care.ExportTestingAsync(query, ct);
        static string Cell(string? value)
        {
            value ??= "";
            if (value.Length > 0 && ("=+-@".Contains(value.TrimStart().FirstOrDefault()) || "\t\r\n".Contains(value[0]))) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("Patient ID,Patient,Case ID,Testing stage,Source draw,Target date,Status,Urgent,Assigned to,Contact review,Contact,Phone,Email,Mailing address (current residence)\r\n");
        foreach (var r in rows)
            csv.Append(string.Join(',', new[] { r.PersonId.ToString(), r.PatientName, r.ExposureCaseId?.ToString(), TestingDisplay.Kind(r.PlanKind),
                r.AnchorDate?.ToString("yyyy-MM-dd"), r.DueDate?.ToString("yyyy-MM-dd"), TestingDisplay.Status(r.TaskStatus, r.DueDate, clock.Today),
                r.IsUrgent ? "Yes" : "No", r.AssignedToName, r.ContactStatus, r.ContactName, r.Phone, r.Email, r.MailingAddress }.Select(Cell))).Append("\r\n");
        Response.Headers.CacheControl = "no-store";
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "clinical-testing-outreach.csv");
    }
}
