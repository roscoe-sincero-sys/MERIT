namespace Merit.Application.Data;

public sealed record TestingOutreachQuery(string DueFilter = "all", string? PlanKind = null, Guid? AssignedToUserId = null,
    string TaskStatus = "open", int Page = 1);

public sealed class TestingOutreachRow
{
    public Guid WorkTaskId { get; init; }
    public Guid PersonId { get; init; }
    public Guid? ExposureCaseId { get; init; }
    public string PatientName { get; init; } = "";
    public string PlanKind { get; init; } = "";
    public string TaskStatus { get; init; } = "";
    public DateOnly? DueDate { get; init; }
    public DateOnly? AnchorDate { get; init; }
    public bool IsUrgent { get; init; }
    public string? AssignedToName { get; init; }
    public string ContactStatus { get; init; } = "";
    public string? ContactName { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? MailingAddress { get; init; }
    public int TotalCount { get; init; }
}

public static class TestingDisplay
{
    public static string Status(string taskStatus, DateOnly? target, DateOnly today) => taskStatus switch
    {
        "done" => "Completed",
        "dismissed" => "Dismissed",
        _ when target is null => "Clinical review required",
        _ when target < today => "Overdue",
        _ when target == today => "Due today",
        _ when target <= today.AddDays(7) => "Due within 7 days",
        _ => "Scheduled",
    };

    public static string Kind(string? value) => value switch
    {
        "screen12" => "12-month screening",
        "screen24" => "24-month screening",
        "confirmation" => "Confirmatory venous test",
        "early" => "Early follow-up / below-reference monitoring",
        "later" => "Ongoing follow-up after reviewed decline",
        "discharge" => "Closure readiness review",
        _ => "Clinical review required",
    };
}
