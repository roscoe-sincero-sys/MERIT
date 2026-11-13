using Merit.Application.Data;

namespace Merit.Application.Services;

/// <summary>Reviewable drafts, never active seeds. Agency approval and an explicit holiday policy remain required.</summary>
public static class MarylandPolicyDrafts
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["mdChild"] = "Child under 6: parent/legal guardian", ["mdPregnantMinor"] = "Pregnant minor: legal caregiver",
        ["mdPregnantAdult"] = "Pregnant adult: patient", ["mdOwnerChild"] = "Child: residence owner",
        ["mdOwnerPregnant"] = "Pregnancy: residence owner", ["mdAffectedChild"] = "Child: affected-property owners",
        ["mdAffectedPregnant"] = "Pregnancy: affected-property owners", ["mdLabElevated"] = "Lab: elevated result reporting",
        ["mdLabBelow"] = "Lab: below-reference reporting", ["mdChild2022"] = "Historical child notice: July–October 2022",
        ["mdChild2023"] = "Historical child notice: October 2022–December 2023",
        // Issue #293: section 6-304 names the property owner as well as the caregiver in both historical periods.
        ["mdOwnerChild2022"] = "Historical child residence-owner notice: July–October 2022",
        ["mdOwnerChild2023"] = "Historical child residence-owner notice: October 2022–December 2023",
    };

    /// <summary>The first receipt date any shipped section 6-304 notice draft covers. Earlier receipts have no draft (issue #293).</summary>
    public static readonly DateOnly EarliestNoticeDraft = new(2022, 7, 1);

    /// <summary>
    /// Issue #292: the Maryland reference-level history (section 6-801 / COMAR 26.16.08.03) as case-level drafts for the
    /// two populations the notice drafts cover. Without a case level a population can never open a case.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ThresholdNames = new Dictionary<string, string>
    {
        ["mdRefChild2020"] = "Child under 6: reference level 5 µg/dL, July 2020–December 2023",
        ["mdRefChild2024"] = "Child under 6: reference level 3.5 µg/dL, from January 2024",
        ["mdRefPregnant2020"] = "Pregnancy: reference level 5 µg/dL, July 2020–December 2023",
        ["mdRefPregnant2024"] = "Pregnancy: reference level 3.5 µg/dL, from January 2024",
    };

    public const string ThresholdAuthority = "Md. Environment 6-801 / COMAR 26.16.08.03 reference level; program approval required";

    /// <summary>One case-level draft, or null for an unknown key. The caller supplies the lead whole-blood specimen.</summary>
    public static ThresholdDraft? GetThreshold(string? key)
    {
        if (key is null || !ThresholdNames.ContainsKey(key)) return null;
        bool pregnant = key.Contains("Pregnant", StringComparison.Ordinal), early = key.EndsWith("2020", StringComparison.Ordinal);
        return new ThresholdDraft(pregnant ? "pregnant" : "child", pregnant ? null : 71, early ? 5m : 3.5m,
            early ? new DateOnly(2020, 7, 1) : new DateOnly(2024, 1, 1), early ? new DateOnly(2023, 12, 31) : null, ThresholdAuthority);
    }

    public static Dictionary<string, string> Get(string? key)
    {
        if (key is null || !Names.TryGetValue(key, out var label)) return [];
        bool lab = key.StartsWith("mdLab", StringComparison.Ordinal), pregnant = key.Contains("Pregnant", StringComparison.Ordinal);
        bool owner = key.StartsWith("mdOwner", StringComparison.Ordinal) || key.StartsWith("mdAffected", StringComparison.Ordinal);
        bool period2022 = key.EndsWith("2022", StringComparison.Ordinal), period2023 = key.EndsWith("2023", StringComparison.Ordinal);
        var d = new Dictionary<string, string>
        {
            ["ruleName"] = "Maryland — " + label, ["population"] = pregnant ? "pregnant" : "all",
            ["minimumValue"] = key == "mdLabBelow" ? "0" : period2022 ? "5" : "3.5",
            ["recipientKind"] = lab ? "department" : owner ? "owner" : key == "mdPregnantAdult" ? "patient" : "caregiver",
            ["rulePurpose"] = lab ? "labReporting" : "notification",
            ["eligibilityMode"] = lab ? "mdReporting" : key.StartsWith("mdAffected", StringComparison.Ordinal) ? "mdAffectedOwner" : owner ? "mdOwner" : "mdEbl",
            ["dueInDays"] = lab ? key == "mdLabBelow" ? "14" : "1" : "10",
            ["dayBasis"] = key == "mdLabBelow" ? "calendar" : "business", ["timeZoneId"] = "Eastern Standard Time",
            ["effectiveFrom"] = period2022 ? "2022-07-01" : period2023 ? "2022-10-28" : "2024-01-01",
            ["approvalReference"] = lab ? "COMAR 26.02.01.04; program approval required" : key.StartsWith("mdAffected", StringComparison.Ordinal)
                ? "Environment 6-846 / 6-801; 10-day operational window requires program approval" : "Environment 6-304 / COMAR 26.16.08.05; program approval required"
        };
        if (lab)
        {
            d["maximumAgeMonths"] = "227";
            d["requiredFieldsCsv"] = "firstName,lastName,dob,countryOfBirth,sex,race,ethnicity,addressLine1,city,state,zip5,countyFips,phone,guardianName,sampleType,collectedDate,orderingProviderNpi,orderingProviderName,orderingProviderAddress,orderingProviderPhone";
        }
        else if (!pregnant) d["maximumAgeMonths"] = "71";
        if (key == "mdPregnantMinor") d["maximumAgeMonths"] = "215";
        if (key == "mdPregnantAdult") d["minimumAgeMonths"] = "216";
        if (key == "mdLabBelow") d["maximumValue"] = "3.5";
        if (period2022) d["effectiveTo"] = "2022-10-27";
        if (period2023) d["effectiveTo"] = "2023-12-31";
        return d;
    }
}

/// <summary>A case-level draft for the threshold form (issue #292).</summary>
public sealed record ThresholdDraft(string Population, int? AgeMaxMonths, decimal LowerValue, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Authority);

/// <summary>
/// Issue #292: a notification rule for a population that has no case level produces notice reviews but can never open
/// a case, so no assignment, investigation or closure follows. This finds such rules so the configuration pages can say so.
/// </summary>
public static class PolicyCoverage
{
    /// <summary>One message per active Department notification rule whose population has no active case level that overlaps it in time.</summary>
    public static IReadOnlyList<string> Gaps(IEnumerable<NotificationRuleRow> rules, IEnumerable<ThresholdRow> thresholds)
    {
        var caseLevels = thresholds.Where(t => t.IsActive && t.ThresholdType == "case").ToList();
        var gaps = new List<string>();
        foreach (var rule in rules.Where(r => r.IsActive && r.RulePurpose == "notification").OrderBy(r => r.RuleName, StringComparer.Ordinal))
        {
            bool specific = rule.Population is "child" or "adult" or "pregnant";
            bool covered = caseLevels.Any(t => t.AnalyteCode == rule.AnalyteCode && t.Specimen == rule.Specimen
                && (t.Population == "all" || !specific || t.Population == rule.Population)
                && t.EffectiveFrom <= (rule.EffectiveTo ?? DateOnly.MaxValue) && (t.EffectiveTo ?? DateOnly.MaxValue) >= rule.EffectiveFrom);
            if (!covered)
            {
                gaps.Add($"\"{rule.RuleName}\" is active, but no active case level for {rule.AnalyteCode} / {rule.Specimen}"
                    + (specific ? $" covers the {rule.Population} population" : string.Empty)
                    + " in its effective period. Matching results get notice reviews but no case, assignment or investigation request.");
            }
        }

        return gaps;
    }
}
