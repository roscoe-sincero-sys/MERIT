namespace Merit.Application.Security;

/// <summary>
/// Permission codes. Each is also the name of an authorization policy in Merit.Web, and the stored procedures check the
/// same codes through auth.udfHasPermission, so a permission is enforced in the controller, the service and the
/// database.
/// </summary>
public static class Permissions
{
    public const string PersonView = "person.view";
    public const string PersonEdit = "person.edit";
    public const string PersonViewIdentifiers = "person.view_identifiers";
    public const string PersonMerge = "person.merge";
    public const string ResultView = "result.view";
    public const string ResultEdit = "result.edit";
    public const string ResultReleaseHold = "result.release_hold";
    public const string CaseView = "case.view";
    public const string CaseOpen = "case.open";
    public const string CaseEdit = "case.edit";
    public const string InvestigationEdit = "investigation.edit";
    public const string TaskView = "task.view";
    public const string TaskEdit = "task.edit";
    public const string IntakeUpload = "intake.upload";
    public const string IntakeConfigure = "intake.configure";
    public const string DataExport = "data.export";
    public const string MatchingConfigure = "matching.configure";
    public const string ExtractRun = "extract.run";
    public const string ExtractSubmit = "extract.submit";
    public const string ConfigEdit = "config.edit";
    public const string TenantAdminister = "tenant.administer";
    public const string AuditView = "audit.view";
    public const string PlatformAdminister = "platform.administer";

    /// <summary>Policy for actions any signed-in user may take (their own account, sign-out).</summary>
    public const string SignedIn = "signedIn";

    public static IReadOnlyList<string> All { get; } =
    [
        PersonView, PersonEdit, PersonViewIdentifiers, PersonMerge, ResultView, ResultEdit, ResultReleaseHold, CaseView,
        CaseOpen, CaseEdit, InvestigationEdit, TaskView, TaskEdit, IntakeUpload, IntakeConfigure, DataExport,
        MatchingConfigure, ExtractRun, ExtractSubmit, ConfigEdit, TenantAdminister, AuditView, PlatformAdminister,
    ];
}
