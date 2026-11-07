using Merit.Application.Security;

namespace Merit.Web.Infrastructure;

public sealed record UsageScreen(string Code, string Name, string Route, params string[] Permissions)
{
    public bool Allowed(ICurrentSession session) => session.IsAuthenticated && (Permissions.Length == 0 || Permissions.Any(session.Has));
}

/// <summary>Only this catalog reaches storage. Never infer telemetry from a URL, heading, query or referrer.</summary>
public static class ScreenCatalog
{
    public static readonly IReadOnlyList<UsageScreen> All =
    [
        new("home", "Home", "/", Permissions.TaskView),
        new("people.search", "Find or add a person", "/search", Permissions.PersonView),
        new("person", "Person", "/people/{id}", Permissions.PersonView),
        new("person.new", "Add a person", "/people/new", Permissions.PersonEdit),
        new("person.edit", "Edit a person", "/people/{id}/edit", Permissions.PersonEdit),
        new("result.new", "Enter a result", "/people/{id}/results/new", Permissions.ResultEdit),
        new("result.saved", "Result saved outcome", "/people/{id}/results", Permissions.ResultEdit),
        new("result", "Lab result", "/results/{id}", Permissions.ResultView),
        new("intake", "Intake", "/intake", Permissions.IntakeUpload),
        new("cases", "Cases", "/cases", Permissions.CaseView),
        new("case", "Case", "/cases/{id}", Permissions.CaseView),
        new("investigations", "Investigations", "/investigations", Permissions.CaseView),
        new("investigation", "Investigation", "/investigations/{id}", Permissions.CaseView),
        new("places", "Places", "/places", Permissions.PersonView),
        new("reporters", "Reporters", "/reporters", Permissions.PersonView),
        new("reporters.import", "Import reporter registry", "/reporters/import", Permissions.RegistryEdit),
        new("extracts", "Extracts", "/extracts", Permissions.ExtractRun),
        new("extracts.findings", "Extract findings", "/extracts/{id}/findings", Permissions.ExtractRun),
        new("config.thresholds", "Reference levels", "/config/thresholds"),
        new("config.threshold", "Edit reference level", "/config/thresholds/{id}", Permissions.ConfigEdit),
        new("config.threshold.new", "Add reference level", "/config/thresholds/new", Permissions.ConfigEdit),
        new("config.settings", "Jurisdiction settings", "/config/settings"),
        new("config.analytes", "Analytes", "/config/analytes"),
        new("users", "Users", "/admin/users", Permissions.TenantAdminister),
        new("user", "Edit user", "/admin/users/{id}", Permissions.TenantAdminister),
        new("user.new", "Add user", "/admin/users/new", Permissions.TenantAdminister),
        new("audit", "Audit log", "/admin/audit", Permissions.AuditView),
        new("tenants", "Tenants", "/admin/tenants", Permissions.TenantCreate),
        new("signin.activity", "Sign-in activity", "/admin/sign-in-activity", Permissions.TenantAdminister),
        new("signin.unknown", "Unknown sign-in names", "/admin/sign-in-activity/unknown-names", Permissions.SecurityInvestigate),
        new("profiles", "My profiles", "/account/profiles"),
        new("profile.edit", "Edit profile", "/account/profiles/{id}/edit"),
        new("profile.new", "Add profile", "/account/profiles/new"),
        new("account.security", "Sign-in methods", "/account/security"),
        new("account.email", "Confirm email codes", "/account/security/email"),
        new("account.password", "Required password change", "/account/password/required"),
        new("account.recovery", "Save recovery codes", "/account/recovery"),
        new("help", "User guides", "/help"),
        new("help.operations", "Daily operations guide", "/help/guides/operations-guide.html"),
        new("help.access", "Access and profiles guide", "/help/guides/access-guide.html"),
        new("help.registry", "Reporter registry guide", "/help/guides/npi-clia-import-guide.html"),
        new("queues", "Work queues", "/app/queues", Permissions.TaskView),
        new("held", "Held records", "/app/held", Permissions.ResultReleaseHold),
        new("match", "Person matching review", "/app/match-review", Permissions.ResultReleaseHold),
        new("results", "Results", "/app/results", Permissions.ResultView),
        new("timeline", "Person timeline", "/app/people/{id}/timeline", Permissions.PersonView),
        new("map", "Case map", "/app/map", Permissions.CaseView),
        new("clusters", "Clusters", "/app/clusters", Permissions.CaseView),
        new("cluster", "Cluster", "/app/clusters/{id}", Permissions.CaseView),
        new("reports", "Reports", "/app/reports", Permissions.ResultView, Permissions.CaseView),
        new("usage", "Usage", "/admin/usage", Permissions.UsageView),
        new("usage.session", "Usage session", "/admin/usage/sessions/{reference}", Permissions.UsageView),
    ];

    private static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Home.Index"] = "home", ["Search.Index"] = "people.search", ["People.New"] = "person.new", ["People.Edit"] = "person.edit",
        ["Results.New"] = "result.new", ["Results.Details"] = "result", ["Intake.Index"] = "intake",
        ["Cases.Index"] = "cases", ["Cases.Details"] = "case", ["Investigations.Index"] = "investigations",
        ["Investigations.Details"] = "investigation", ["Places.Index"] = "places", ["Reporters.Index"] = "reporters",
        ["Reporters.Import"] = "reporters.import", ["Extracts.Index"] = "extracts", ["Extracts.Findings"] = "extracts.findings",
        ["Config.Thresholds"] = "config.thresholds", ["Config.EditThreshold"] = "config.threshold",
        ["Config.Settings"] = "config.settings", ["Config.Analytes"] = "config.analytes", ["Admin.Users"] = "users",
        ["Admin.EditUser"] = "user", ["Admin.Audit"] = "audit", ["Tenants.Index"] = "tenants",
        ["SignInActivity.Index"] = "signin.activity", ["SignInActivity.UnknownNames"] = "signin.unknown",
        ["Profiles.Index"] = "profiles", ["Profiles.Edit"] = "profile.edit", ["Account.SignInMethods"] = "account.security",
        ["Account.ConfirmEmailCodes"] = "account.email", ["Account.RequiredPassword"] = "account.password",
        ["Account.RecoveryCodes"] = "account.recovery", ["Help.Index"] = "help", ["Usage.Index"] = "usage",
        ["Usage.Session"] = "usage.session",
    };

    public static UsageScreen? Find(string? code) => All.FirstOrDefault(s => s.Code == code);
    public static string Name(string code) => Find(code)?.Name ?? "Unknown screen";
    public static string Route(string code) => Find(code)?.Route ?? "";
    public static string PathName(string codes) => string.Join(" → ", codes.Split(" > ").Select(Name));

    public static UsageScreen? ForAction(string controller, string action, string? guide = null, bool isNew = false)
    {
        if (isNew && controller == "Profiles" && action == "Edit") return Find("profile.new");
        if (isNew && controller == "Config" && action == "EditThreshold") return Find("config.threshold.new");
        if (isNew && controller == "Admin" && action == "EditUser") return Find("user.new");
        if (controller == "People" && action is "Details" or "Header" or "Relationships" or "Results" or "Cases" or "Employment" or "Sources" or "Pregnancies")
            return Find("person");
        if (controller == "Help" && action == "Guide")
            return Find(guide switch { "operations-guide.html" => "help.operations", "access-guide.html" => "help.access", "npi-clia-import-guide.html" => "help.registry", _ => null });
        return Actions.TryGetValue(controller + "." + action, out var code) ? Find(code) : null;
    }
}
