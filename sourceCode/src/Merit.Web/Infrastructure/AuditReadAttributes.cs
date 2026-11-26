namespace Merit.Web.Infrastructure;

/// <summary>
/// Marks a GET controller action or API endpoint as audited by its service/filter layer (#331). Verified by the
/// audit-coverage inventory unit test.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditedReadAttribute(string action, string entityType) : Attribute
{
    public string Action { get; } = action;
    public string EntityType { get; } = entityType;
}

/// <summary>
/// Explicitly exempts a GET action requiring a patient-data or security-log permission from writing a ledger row,
/// with a reviewed reason (e.g., blank create form, static reference lookup, or template download) (#331).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditExemptAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
