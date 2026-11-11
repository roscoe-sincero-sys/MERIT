namespace Merit.Web.Infrastructure;

/// <summary>
/// The environment and data-mode indicator (issue #224), from explicit deployment configuration only:
/// <c>Deployment:Environment</c> (Live, Training, Test, Demo or Development), <c>Deployment:AgencyName</c> and
/// <c>Deployment:SyntheticData</c>. A non-live environment is named in a banner on every screen, before anyone acts on
/// a record. The synthetic-data claim needs both a non-live environment and SyntheticData = true, so a live or
/// unconfigured deployment can never show it. Nothing here comes from host names, connection strings or machine state.
/// </summary>
public sealed record DeploymentIndicator(string? Environment, string? AgencyName, bool SyntheticData)
{
    public const string Section = "Deployment";

    private static readonly string[] Known = ["Live", "Training", "Test", "Demo", "Development"];

    public static DeploymentIndicator From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        string? configured = section["Environment"]?.Trim();
        string? environment = Known.FirstOrDefault(k => string.Equals(k, configured, StringComparison.OrdinalIgnoreCase));
        string? agency = section["AgencyName"]?.Trim();
        return new DeploymentIndicator(environment, string.IsNullOrEmpty(agency) ? null : agency, section.GetValue("SyntheticData", false));
    }

    /// <summary>Live, or not configured: no banner and no claim about the data.</summary>
    public bool IsLive => Environment is null or "Live";

    /// <summary>Only a non-live environment explicitly configured for synthetic records may say so.</summary>
    public bool ShowsSyntheticClaim => !IsLive && SyntheticData;

    /// <summary>"TRAINING environment · Maryland Department of the Environment · Synthetic data only", or null when live.</summary>
    public string? Banner => IsLive ? null : string.Join(" · ", new[]
    {
        $"{Environment!.ToUpperInvariant()} environment",
        AgencyName,
        ShowsSyntheticClaim ? "Synthetic data only" : "Not for live casework",
    }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>The footer's identifying part; the audit statement follows it on every installation.</summary>
    public string Footer => string.Join(" · ", new[]
    {
        "MERIT",
        AgencyName,
        IsLive ? null : $"{Environment} environment",
        ShowsSyntheticClaim ? "Synthetic demonstration data only" : null,
    }.Where(p => !string.IsNullOrEmpty(p)));
}
