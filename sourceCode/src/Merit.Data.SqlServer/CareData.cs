using Merit.Application.Data;

namespace Merit.Data.SqlServer;

public sealed class CareData(Db db) : ICareData
{
    public Task<IReadOnlyList<PatientActivityRow>> ActivitiesAsync(Guid personId, Guid? exposureCaseId, int pageNumber, CancellationToken ct)
        => db.QueryAsync<PatientActivityRow>("dbo.uspListPatientActivity", new { personId, exposureCaseId, pageNumber }, ct);
    public async Task<int> RecordAsync(PatientActivitySave row, CancellationToken ct)
        => (await db.QuerySingleAsync<ActivityRecorded>("dbo.uspRecordPatientActivity", row, ct).ConfigureAwait(false))?.FailedContactAttempts ?? 0;
    private sealed class ActivityRecorded { public int FailedContactAttempts { get; init; } }
    public Task LinkAsync(Guid personId, Guid exposureCaseId, Guid recordId, CancellationToken ct)
        => db.ExecuteAsync("dbo.uspLinkPatientRecord", new { personId, exposureCaseId, recordId, recordKind = "activity" }, ct);
    public Task<IReadOnlyList<PatientDocumentRow>> DocumentsAsync(Guid personId, Guid? exposureCaseId, int pageNumber, CancellationToken ct)
        => db.QueryAsync<PatientDocumentRow>("dbo.uspListPatientDocuments", new { personId, exposureCaseId, pageNumber }, ct);
    public Task<PatientDocumentRow?> DocumentAsync(Guid personId, Guid personDocumentId, CancellationToken ct)
        => db.QuerySingleAsync<PatientDocumentRow>("dbo.uspGetPatientDocument", new { personId, personDocumentId }, ct);
    public Task SaveDocumentAsync(PatientDocumentSave row, CancellationToken ct) => db.ExecuteAsync("dbo.uspSavePatientDocument", row, ct);
    public Task<IReadOnlyList<NotificationSummaryRow>> SummaryAsync(CancellationToken ct) => db.QueryAsync<NotificationSummaryRow>("dbo.uspNotificationComplianceSummary", null, ct);
    public Task<IReadOnlyList<NotificationRow>> NotificationsAsync(Guid? personId, Guid? exposureCaseId, int pageNumber, CancellationToken ct)
        => db.QueryAsync<NotificationRow>("dbo.uspListNotificationObligations", new { personId, exposureCaseId, pageNumber }, ct);
    public Task ReviewAsync(Guid personId, Guid notificationObligationId, bool applies, string reason, CancellationToken ct)
        => db.ExecuteAsync("dbo.uspReviewNotificationObligation", new { personId, notificationObligationId, applies, reason }, ct);
    public Task WithdrawAsync(Guid personId, Guid notificationObligationId, string reason, CancellationToken ct)
        => db.ExecuteAsync("dbo.uspWithdrawNotificationEvidence", new { personId, notificationObligationId, reason }, ct);
    public async Task<int> ReconcileAsync(Guid? labResultId, Guid? notificationRuleId, Guid? personId, CancellationToken ct)
        => (await db.QuerySingleAsync<Reconciled>("dbo.uspReconcileNotifications", new { labResultId, notificationRuleId, personId }, ct).ConfigureAwait(false))?.CreatedCount ?? 0;
    private sealed class Reconciled { public int CreatedCount { get; init; } }
    public Task<IReadOnlyList<NotificationRuleRow>> RulesAsync(CancellationToken ct) => db.QueryAsync<NotificationRuleRow>("config.uspListNotificationRules", null, ct);
    public Task SaveRuleAsync(NotificationRuleSave row, CancellationToken ct) => db.ExecuteAsync("config.uspSaveNotificationRule", row, ct);
    public Task RetireRuleAsync(Guid notificationRuleId, CancellationToken ct) => db.ExecuteAsync("config.uspRetireNotificationRule", new { notificationRuleId }, ct);
}
