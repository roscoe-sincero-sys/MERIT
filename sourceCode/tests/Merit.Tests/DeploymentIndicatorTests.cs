using Merit.Web.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Merit.Tests;

/// <summary>Issue #224: the environment and data-mode indicator comes only from explicit deployment configuration.</summary>
public sealed class DeploymentIndicatorTests
{
    private static DeploymentIndicator From(params (string Key, string Value)[] values)
        => DeploymentIndicator.From(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>("Deployment:" + v.Key, v.Value))).Build());

    [Fact]
    public void UnconfiguredDeploymentClaimsNothing()
    {
        var d = From();
        Assert.True(d.IsLive);
        Assert.Null(d.Banner);
        Assert.False(d.ShowsSyntheticClaim);
        Assert.Equal("MERIT", d.Footer);
    }

    [Fact]
    public void LiveCannotClaimSyntheticDataEvenWhenMisconfigured()
    {
        var d = From(("Environment", "live"), ("SyntheticData", "true"), ("AgencyName", "Maryland Department of the Environment"));
        Assert.Null(d.Banner);
        Assert.False(d.ShowsSyntheticClaim);
        Assert.Equal("MERIT · Maryland Department of the Environment", d.Footer);
        Assert.DoesNotContain("Synthetic", d.Footer, StringComparison.Ordinal);
    }

    [Fact]
    public void TrainingWithSyntheticDataNamesItselfOnEveryScreen()
    {
        var d = From(("Environment", "Training"), ("SyntheticData", "true"), ("AgencyName", "Maryland Department of the Environment"));
        Assert.Equal("TRAINING environment · Maryland Department of the Environment · Synthetic data only", d.Banner);
        Assert.Equal("MERIT · Maryland Department of the Environment · Training environment · Synthetic demonstration data only", d.Footer);
    }

    [Fact]
    public void TestWithoutSyntheticFlagWarnsInsteadOfClaimingSyntheticData()
    {
        var d = From(("Environment", "Test"));
        Assert.Equal("TEST environment · Not for live casework", d.Banner);
        Assert.False(d.ShowsSyntheticClaim);
    }

    [Fact]
    public void UnknownEnvironmentNamesAreTreatedAsLive()
    {
        // A typo must not produce a banner built from free text, nor a synthetic claim.
        var d = From(("Environment", "Prod-East-02"), ("SyntheticData", "true"));
        Assert.True(d.IsLive);
        Assert.Null(d.Banner);
    }
}
