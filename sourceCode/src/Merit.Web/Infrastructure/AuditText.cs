using Merit.Application.Reference;
using Merit.Application.Security;

namespace Merit.Web.Infrastructure;

/// <summary>
/// The audit log in words (issue #45): actions and record types as a person says them, and a link to the record when the
/// viewer can open it. The ledger itself holds codes and ids only.
/// </summary>
public static class AuditText
{
    public static readonly IReadOnlyList<(string Code, string Label)> Actions =
    [
        ("view", "Viewed"), ("create", "Created"), ("update", "Changed"), ("export", "Exported"), ("search", "Searched"),
        ("signIn", "Signed in"), ("signInFailed", "Sign-in failed"), ("signOut", "Signed out"),
        ("mfaEnrolled", "Set up multi-factor authentication"),
    ];

    /// <summary>"report" stands for every report ("report:testingCounts").</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> RecordTypes =
    [
        ("person", "Person"), ("labResult", "Lab result"), ("exposureCase", "Case"), ("investigation", "Investigation"),
        ("cluster", "Cluster"), ("stagingRow", "Laboratory file row"), ("workTask", "Work queue tasks"), ("caseMap", "Case map"),
        ("report", "Report"), ("extractRun", "Extract"), ("appUser", "User account"), ("viewAsUser", "User account (view as)"),
        ("personOutsideScope", "Person outside your access"), ("auditEvent", "Audit log"),
    ];

    private static readonly Dictionary<string, string> ActionNames = Actions.ToDictionary(a => a.Code, a => a.Label, StringComparer.Ordinal);
    private static readonly Dictionary<string, string> TypeNames = RecordTypes.ToDictionary(t => t.Code, t => t.Label, StringComparer.Ordinal);

    public static string Action(string code) => ActionNames.TryGetValue(code, out var label) ? label : DisplayNames.Split(code);

    public static string RecordType(string code)
        => code.StartsWith("report:", StringComparison.Ordinal) ? "Report: " + DisplayNames.Split(code["report:".Length..])
            : TypeNames.TryGetValue(code, out var label) ? label : DisplayNames.Split(code);

    /// <summary>The record's page, when there is one and the viewer may open it; the page checks access again.</summary>
    public static string? Link(string entityType, Guid? id, ICurrentSession session) => id is not Guid g ? null : entityType switch
    {
        "person" when session.Has(Permissions.PersonView) => $"/people/{g}",
        "exposureCase" when session.Has(Permissions.CaseView) => $"/cases/{g}",
        "investigation" when session.Has(Permissions.CaseView) => $"/investigations/{g}",
        "cluster" when session.Has(Permissions.CaseView) => $"/app/clusters/{g}",
        "labResult" when session.Has(Permissions.ResultView) => $"/results/{g}",
        "appUser" or "viewAsUser" when session.Has(Permissions.TenantAdminister) => $"/admin/users/{g}",
        _ => null,
    };
}
