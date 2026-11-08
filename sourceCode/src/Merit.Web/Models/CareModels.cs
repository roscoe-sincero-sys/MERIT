using Merit.Application.Data;
using Merit.Application.Services;

namespace Merit.Web.Models;

public sealed record CareWorkspaceModel(CarePage Care, PersonPage Person, NotificationRow? Completing);
public sealed record NotificationPolicyModel(IReadOnlyList<NotificationRuleRow> Rules, IReadOnlyList<SelectOption> Specimens);
public sealed record NotificationReportModel(IReadOnlyList<NotificationRow> Rows, int Page, IReadOnlyList<NotificationSummaryRow> Summary);
public sealed record CarePagingModel(string Root, Guid? CaseId, string Key, int Page, int Total, string Label);
