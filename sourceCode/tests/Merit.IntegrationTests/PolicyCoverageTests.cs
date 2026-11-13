using System.Net;
using Merit.Application.Services;

namespace Merit.IntegrationTests;

/// <summary>Issue #292: the configuration pages say when a notification rule's population has no case level.</summary>
public sealed partial class AdministrationAndTestingPolicyTests
{
    [SqlFact]
    public async Task ConfigurationPagesWarnWhileANotificationRulePopulationHasNoCaseLevel()
    {
        _ = fixture.Ids;
        var root = await NewRoot("MD"); // no seeded thresholds
        var specimen = await ConfigurationSpecimen();
        var (account, admin) = await User(root, "administrator", "supervisor");
        await using (account)
        using (admin)
        {
            async Task<string> Page(string url) => WebUtility.HtmlDecode(await admin.Http.GetStringAsync(url));
            async Task Rule(string key)
            {
                await admin.Http.GetStringAsync("/config/notification-rules?template=" + key);
                var values = MarylandPolicyDrafts.Get(key).Select(d => (d.Key, (object?)d.Value)).ToList();
                values.AddRange([("notificationRuleId", Guid.NewGuid()), ("analyteSpecimenId", specimen), ("calendarName", "Synthetic weekdays only"), ("holidays", ""), ("approved", "true")]);
                await Saved(await admin.NoRedirect.PostAsync("/config/notification-rules", Form(values.ToArray())));
            }
            async Task Level(string key)
            {
                var draft = MarylandPolicyDrafts.GetThreshold(key)!;
                string form = await Page("/config/thresholds/new?template=" + key);
                Assert.Contains(MarylandPolicyDrafts.ThresholdAuthority, form, StringComparison.Ordinal);
                Assert.Contains("value=\"" + draft.EffectiveFrom.ToString("yyyy-MM-dd") + "\"", form, StringComparison.Ordinal);
                await Saved(await admin.NoRedirect.PostAsync("/config/thresholds", Form(("ThresholdId", Guid.NewGuid()), ("AnalyteSpecimenId", specimen), ("Population", draft.Population),
                    ("AgeMaxMonths", draft.AgeMaxMonths), ("ThresholdType", "case"), ("LowerValue", draft.LowerValue.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Comparator", ">="),
                    ("IsActive", "true"), ("EffectiveFrom", draft.EffectiveFrom.ToString("yyyy-MM-dd")), ("EffectiveTo", draft.EffectiveTo?.ToString("yyyy-MM-dd") ?? ""), ("Authority", draft.Authority))));
            }

            // Nothing active: no warning, and the drafts are offered on both pages.
            string thresholds = await Page("/config/thresholds");
            Assert.DoesNotContain("id=\"coverage-gaps\"", thresholds, StringComparison.Ordinal);
            foreach (var key in MarylandPolicyDrafts.ThresholdNames.Keys) Assert.Contains("/config/thresholds/new?template=" + key, thresholds, StringComparison.Ordinal);
            string policies = await Page("/config/notification-rules");
            Assert.Contains("mdOwnerChild2022", policies, StringComparison.Ordinal);
            Assert.Contains("Historical coverage", policies, StringComparison.Ordinal);
            Assert.Contains("July 1, 2022", policies, StringComparison.Ordinal);

            // A child rule and a pregnancy rule with only the child level: the pregnancy rule is named on both pages.
            await Rule("mdChild"); await Rule("mdPregnantAdult"); await Level("mdRefChild2024");
            foreach (string url in new[] { "/config/thresholds", "/config/notification-rules" })
            {
                string page = await Page(url);
                Assert.Contains("id=\"coverage-gaps\"", page, StringComparison.Ordinal);
                Assert.Contains("\"Maryland — Pregnant adult: patient\" is active, but no active case level", page, StringComparison.Ordinal);
                Assert.DoesNotContain("\"Maryland — Child under 6: parent/legal guardian\" is active, but", page, StringComparison.Ordinal);
            }

            // The pregnancy level for a period that ended before the rule starts does not cover it; the current one does.
            await Level("mdRefPregnant2020");
            Assert.Contains("id=\"coverage-gaps\"", await Page("/config/thresholds"), StringComparison.Ordinal);
            await Level("mdRefPregnant2024");
            Assert.DoesNotContain("id=\"coverage-gaps\"", await Page("/config/thresholds"), StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"coverage-gaps\"", await Page("/config/notification-rules"), StringComparison.Ordinal);
        }
    }
}
