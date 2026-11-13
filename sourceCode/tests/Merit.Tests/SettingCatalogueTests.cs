using Merit.Application.Reference;
using Merit.Web.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Merit.Tests;

/// <summary>Issue #289: one catalogue decides which settings exist, what values they take and whether they are in use.</summary>
public sealed class SettingCatalogueTests
{
    [Theory]
    [InlineData("sessionTimeoutMinutes", "5")]
    [InlineData("sessionTimeoutMinutes", "60")]
    [InlineData("passwordMinLength", "15")]
    [InlineData("adultAgeCutoffYears", "18")]
    [InlineData("siteInvestigationMinHours", "0.5")]
    [InlineData("siteInvestigationMinHours", "7.25")]
    [InlineData("siteInvestigationMinHours", "NOTSET")]
    [InlineData("clusterMinCount", "NOTSET")]
    [InlineData("confirmingMethods", "icpMs")]
    [InlineData("confirmingMethods", "graphiteFurnaceAa,icpMs")]
    [InlineData("primaryResidencePolicy", "move")]
    [InlineData("cblsEnabled", "false")]
    [InlineData("statePostalCode", "MD")]
    [InlineData("stateFips", "24")]
    [InlineData("testingPolicyVersion", "none")]
    [InlineData("adultCaseOwnerTenantCode", "ANY-CODE")] // existence is checked by the database
    public void AcceptsValuesInsideTheDefinition(string key, string value) => Assert.Null(SettingCatalogue.Problem(key, value));

    [Theory]
    [InlineData("sessionTimeoutMinutes", "4")]
    [InlineData("sessionTimeoutMinutes", "61")]
    [InlineData("sessionTimeoutMinutes", "-5")]
    [InlineData("sessionTimeoutMinutes", "abc")]
    [InlineData("sessionTimeoutMinutes", " 30")]
    [InlineData("sessionTimeoutMinutes", "3٠")]
    [InlineData("sessionTimeoutMinutes", "NOTSET")]
    [InlineData("sessionTimeoutMinutes", "99999999999999999999")]
    [InlineData("passwordMinLength", "14")]
    [InlineData("adultAgeCutoffYears", "banana")]
    [InlineData("pregnancyMaxWeeks", "0")]
    [InlineData("siteInvestigationMinHours", "1.234")]
    [InlineData("siteInvestigationMinHours", "1.2.3")]
    [InlineData("siteInvestigationMinHours", ".")]
    [InlineData("siteInvestigationMinHours", "169")]
    [InlineData("confirmingMethods", "icpMs,icpMs")]
    [InlineData("confirmingMethods", "icpMs, graphiteFurnaceAa")]
    [InlineData("confirmingMethods", "ICPMS")]
    [InlineData("primaryResidencePolicy", "Move")]
    [InlineData("cblsEnabled", "yes")]
    [InlineData("statePostalCode", "md")]
    [InlineData("stateFips", "240")]
    [InlineData("clusterMinCount", "")]
    [InlineData("madeUpKey", "1")]
    [InlineData(null, "1")]
    public void RefusesUnknownKeysAndValuesOutsideTheDefinition(string? key, string value) => Assert.NotNull(SettingCatalogue.Problem(key, value));

    [Fact]
    public void SettingsNothingReadsCannotBeChanged()
    {
        string[] unused = ["agencyName", "localNotification", "occupationalReferralAgency", "matchingPreset", "providerLookups", "smsCodesEnabled"];
        Assert.Equal(unused.Order(StringComparer.Ordinal), SettingCatalogue.All.Where(d => !d.InUse).Select(d => d.Key).Order(StringComparer.Ordinal));
        Assert.All(unused, key => Assert.Equal(SettingCatalogue.NotInUseMessage, SettingCatalogue.Problem(key, "true")));
        Assert.Equal(SettingCatalogue.All.Count, SettingCatalogue.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(60, 60)]
    [InlineData(null, 25)]
    [InlineData(4, 25)]
    [InlineData(61, 25)]
    [InlineData(-5, 25)]
    public void SessionIdleTimeoutIsTheRootSettingWithinBoundsElseTheDeploymentDefault(int? rootSetting, int expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Session:IdleTimeoutMinutes"] = "25" }).Build();
        Assert.Equal(expected, SessionCookie.IdleMinutes(rootSetting, configuration));
    }
}
