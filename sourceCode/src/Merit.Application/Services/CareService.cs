using System.Security.Cryptography;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Domain.Ids;

namespace Merit.Application.Services;

public sealed record CarePage(Guid PersonId, Guid? CaseId, IReadOnlyList<PatientActivityRow> Activities,
    IReadOnlyList<PatientDocumentRow> Documents, IReadOnlyList<NotificationRow> Notifications, int ActivityPage, int DocumentPage, int NotificationPage);

public sealed class CareService(ICareData data, IWorkData work, AuditService audit, ICurrentSession session, IClock clock, CaseService cases)
{
    public async Task<CarePage> PageAsync(Guid personId, Guid? caseId, int activities, int documents, int notifications, CancellationToken ct)
    {
        session.Require(Permissions.PersonView);
        var a = await Paging.ReadAsync(activities, 50, p => data.ActivitiesAsync(personId, caseId, p, ct), r => r.TotalCount).ConfigureAwait(false);
        var d = session.Has(Permissions.DocumentView)
            ? await Paging.ReadAsync(documents, 50, p => data.DocumentsAsync(personId, caseId, p, ct), r => r.TotalCount).ConfigureAwait(false)
            : ((IReadOnlyList<PatientDocumentRow>)[], 0, 1);
        var n = await Paging.ReadAsync(notifications, 50, p => data.NotificationsAsync(personId, caseId, p, ct), r => r.TotalCount).ConfigureAwait(false);
        await audit.ViewedAsync("patientCare", personId, ct).ConfigureAwait(false);
        return new(personId, caseId, a.Rows, d.Item1, n.Rows, a.Page, d.Item3, n.Page);
    }

    public async Task<string?> RecordAsync(PatientActivitySave row, bool confirmed, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit);
        if (row.ExposureCaseId is not null) session.Require(Permissions.CaseEdit);
        var v = new Validator();
        v.Required("note", row.Note, "Description").MaxLength("note", row.Note, 2000, "Description")
            .MaxLength("recipientLabel", row.RecipientLabel, 200, "Recipient")
            .When(row.ActivityType == "notificationCompleted" && !confirmed, "confirmed", "Confirm that notification was actually completed; an attempt is not completion.");
        v.When(!new[] { "contactAttempt", "education", "referral", "medicalManagement", "letter", "note", "notificationCompleted" }.Contains(row.ActivityType), "activityType", "Choose an activity type.")
            .When(!new[] { "internal", "patient", "caregiver", "provider", "owner" }.Contains(row.Audience), "audience", "Choose an audience.")
            .When(!new[] { "internal", "phone", "email", "letter", "inPerson", "portal", "other" }.Contains(row.Channel), "channel", "Choose a delivery channel.")
            .When(!new[] { "reached", "noAnswer", "leftMessage", "wrongNumber", "completed", "other" }.Contains(row.Outcome), "outcome", "Choose an outcome.");
        v.MaxLength("noticeFormReference", row.NoticeFormReference, 300, "Notice form reference");
        v.ThrowIfInvalid();
        int failed = await data.RecordAsync(row, ct).ConfigureAwait(false);
        await audit.CreatedAsync("patientActivity", row.PersonActivityId, ct).ConfigureAwait(false);
        return row.ExposureCaseId is Guid caseId
            ? await cases.ProposeLetterAsync(caseId, failed, row.ActivityType, row.Outcome, ct).ConfigureAwait(false) : null;
    }

    public async Task LinkAsync(Guid personId, Guid caseId, Guid activityId, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit); session.Require(Permissions.CaseEdit);
        await data.LinkAsync(personId, caseId, activityId, ct).ConfigureAwait(false);
        await audit.ChangedAsync("patientActivity", activityId, ct).ConfigureAwait(false);
    }

    public async Task UploadAsync(Guid id, Guid personId, Guid? caseId, Guid? supersedes, string title, string fileName,
        string category, DateOnly date, string source, string? description, byte[] bytes, CancellationToken ct)
    {
        session.Require(Permissions.PersonView); session.Require(Permissions.DocumentEdit);
        if (caseId is not null) session.Require(Permissions.CaseEdit);
        fileName = Path.GetFileName(fileName.Replace('\\', '/'));
        var v = new Validator();
        v.Required("title", title, "Title").MaxLength("title", title, 200, "Title")
            .Required("source", source, "Source").MaxLength("source", source, 200, "Source")
            .Required("file", fileName, "File name").MaxLength("file", fileName, 200, "File name")
            .MaxLength("description", description, 1000, "Description")
            .When(date > clock.Today || date < new DateOnly(1900, 1, 1), "documentDate", "Enter the document's actual date.")
            .When(!new[] { "correspondence", "labReport", "consent", "photo", "other" }.Contains(category), "category", "Choose a category.");
        v.ThrowIfInvalid();
        var type = PatientDocumentValidator.Validate(fileName, bytes);
        await data.SaveDocumentAsync(new(id, personId, title.Trim(), fileName, type, category, date, source.Trim(),
            SHA256.HashData(bytes), bytes, description, caseId, supersedes), ct).ConfigureAwait(false);
        await audit.CreatedAsync("patientDocument", id, ct).ConfigureAwait(false);
    }

    public async Task<PatientDocumentRow> DocumentAsync(Guid personId, Guid id, CancellationToken ct)
    {
        session.Require(Permissions.PersonView); session.Require(Permissions.DocumentView);
        var row = await data.DocumentAsync(personId, id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        await audit.ExportedAsync("patientDocument", id, 1, ct).ConfigureAwait(false);
        return row;
    }

    public async Task ReviewAsync(Guid personId, Guid id, bool applies, string reason, CancellationToken ct, bool criteriaConfirmed = false)
    {
        session.Require(Permissions.PersonEdit); ValidateReason(reason);
        await data.ReviewAsync(personId, id, applies, reason, ct, criteriaConfirmed).ConfigureAwait(false);
        await audit.ChangedAsync("notificationObligation", id, ct).ConfigureAwait(false);
    }
    public async Task WithdrawAsync(Guid personId, Guid id, string reason, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit); ValidateReason(reason);
        await data.WithdrawAsync(personId, id, reason, ct).ConfigureAwait(false);
        await audit.ChangedAsync("notificationObligation", id, ct).ConfigureAwait(false);
    }
    private static void ValidateReason(string reason)
    {
        var v = new Validator(); v.Required("reason", reason, "Reason").MaxLength("reason", reason, 500, "Reason"); v.ThrowIfInvalid();
    }

    public Task<IReadOnlyList<NotificationSummaryRow>> SummaryAsync(CancellationToken ct)
    { session.Require(Permissions.PersonView); return data.SummaryAsync(ct); }
    public async Task<(IReadOnlyList<NotificationRow> Rows, int Page)> NotificationsAsync(int page, CancellationToken ct)
    {
        session.Require(Permissions.PersonView);
        var result = await Paging.ReadAsync(page, 50, p => data.NotificationsAsync(null, null, p, ct), r => r.TotalCount).ConfigureAwait(false);
        await audit.ListedAsync("notificationObligation", result.Rows.Count, ct).ConfigureAwait(false);
        return (result.Rows, result.Page);
    }
    public async Task<IReadOnlyList<NotificationRow>> ExportAsync(CancellationToken ct)
    {
        session.Require(Permissions.PersonView); session.Require(Permissions.DataExport);
        var all = new List<NotificationRow>();
        for (int page = 1; ; page++)
        {
            var rows = await data.NotificationsAsync(null, null, page, ct).ConfigureAwait(false);
            all.AddRange(rows);
            if (rows.Count < 50) break;
        }
        await audit.ExportedAsync("notificationObligation", null, all.Count, ct).ConfigureAwait(false);
        return all;
    }
    public async Task<int> EvaluatePatientAsync(Guid personId, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit);
        return await EvaluateAsync(null, personId, ct).ConfigureAwait(false);
    }
    public async Task<int> EvaluateAsync(Guid? ruleId, Guid? personId, CancellationToken ct)
    {
        int total = 0, count;
        do { count = await data.ReconcileAsync(null, ruleId, personId, ct).ConfigureAwait(false); total += count; } while (count == 500);
        for (int page = 1; ; page++)
        {
            var rows = await data.ReceiptResultsAsync(ruleId, personId, page, ct).ConfigureAwait(false);
            foreach (var row in rows) await data.ReconcileReceiptAsync(row.LabResultId, null, ct).ConfigureAwait(false);
            if (rows.Count < 200) break;
        }
        return total;
    }
    public async Task<(IReadOnlyList<ReceiptWorkRow> Rows, int Page)> ReceiptWorkAsync(Guid? personId, int page, CancellationToken ct, Guid? taskId = null)
    {
        session.Require(Permissions.TaskView);
        var result = await Paging.ReadAsync(page, 50, p => data.ReceiptWorkAsync(personId, p, ct, taskId), r => r.TotalCount).ConfigureAwait(false);
        await audit.ListedAsync("receiptQuality", result.Rows.Count, ct).ConfigureAwait(false);
        return (result.Rows, result.Page);
    }
    public async Task ReceiveInformationAsync(Guid id, DateOnly date, string evidence, bool confirmed, CancellationToken ct)
    {
        if (!session.Has(Permissions.PersonEdit) && !session.Has(Permissions.IntakeUpload)) throw new PermissionDeniedException(Permissions.PersonEdit);
        var v = new Validator(); v.Required("evidence", evidence, "Source evidence").MaxLength("evidence", evidence, 1000, "Source evidence"); v.ThrowIfInvalid();
        await data.ReceiveInformationAsync(id, date, evidence, confirmed, ct).ConfigureAwait(false);
        await audit.ChangedAsync("workTask", id, ct).ConfigureAwait(false);
    }
    public async Task VerifyTimelineAsync(Guid personId, Guid resultId, DateOnly? finalDate, string evidence, bool confirmed, CancellationToken ct, bool applies = true, bool sameDayChecked = false, bool lateConfirmed = false)
    {
        session.Require(Permissions.ResultEdit);
        var v = new Validator(); v.Required("evidence", evidence, "Source evidence").MaxLength("evidence", evidence, 1000, "Source evidence"); v.ThrowIfInvalid();
        await data.VerifyTimelineAsync(personId, resultId, finalDate, evidence, confirmed, ct, applies, sameDayChecked, lateConfirmed).ConfigureAwait(false);
        await audit.ChangedAsync("labResult", resultId, ct).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<NotificationRuleRow>> RulesAsync(CancellationToken ct)
    { session.Require(Permissions.ConfigEdit); return data.RulesAsync(ct); }
    public async Task SaveRuleAsync(NotificationRuleSave row, bool approved, CancellationToken ct)
    {
        session.Require(Permissions.ConfigEdit);
        if (!approved) throw new ValidationException("approved", "Confirm program approval of the thresholds, recipients, dates and holiday calendar before activating this rule.");
        var v = new Validator();
        v.Required("ruleName", row.RuleName, "Rule name").MaxLength("ruleName", row.RuleName, 120, "Rule name")
            .Required("calendarName", row.CalendarName, "Calendar name").MaxLength("calendarName", row.CalendarName, 120, "Calendar name")
            .Required("approvalReference", row.ApprovalReference, "Approval reference").MaxLength("approvalReference", row.ApprovalReference, 300, "Approval reference").MaxLength("requiredFieldsCsv", row.RequiredFieldsCsv, 1000, "Required mapped fields");
        v.ThrowIfInvalid();
        await data.SaveRuleAsync(row, ct).ConfigureAwait(false);
        await audit.CreatedAsync("notificationRule", row.NotificationRuleId, ct).ConfigureAwait(false);
        // Backfill is explicit and retryable from the rule list; subsequent received results evaluate automatically.
    }
    public async Task QueueEvaluationAsync(Guid ruleId, CancellationToken ct)
    {
        session.Require(Permissions.ConfigEdit);
        if (!(await data.RulesAsync(ct).ConfigureAwait(false)).Any(r => r.NotificationRuleId == ruleId)) throw new NotFoundException();
        await work.EnqueueJobAsync(TimeOrderedId.New(), JobTypes.NotificationEvaluation, JsonSerializer.Serialize(new { notificationRuleId = ruleId }), ct).ConfigureAwait(false);
    }
    public async Task RetireAsync(Guid ruleId, CancellationToken ct)
    {
        session.Require(Permissions.ConfigEdit);
        await data.RetireRuleAsync(ruleId, ct).ConfigureAwait(false);
        await audit.ChangedAsync("notificationRule", ruleId, ct).ConfigureAwait(false);
    }
}
