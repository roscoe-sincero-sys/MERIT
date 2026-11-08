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
        ["mdChild2023"] = "Historical child notice: October 2022–December 2023"
    };

    public static Dictionary<string, string> Get(string? key)
    {
        if (key is null || !Names.TryGetValue(key, out var label)) return [];
        bool lab = key.StartsWith("mdLab", StringComparison.Ordinal), pregnant = key.Contains("Pregnant", StringComparison.Ordinal);
        bool owner = key.StartsWith("mdOwner", StringComparison.Ordinal) || key.StartsWith("mdAffected", StringComparison.Ordinal);
        var d = new Dictionary<string, string>
        {
            ["ruleName"] = "Maryland — " + label, ["population"] = pregnant ? "pregnant" : "all",
            ["minimumValue"] = key == "mdLabBelow" ? "0" : key == "mdChild2022" ? "5" : "3.5",
            ["recipientKind"] = lab ? "department" : owner ? "owner" : key == "mdPregnantAdult" ? "patient" : "caregiver",
            ["rulePurpose"] = lab ? "labReporting" : "notification",
            ["eligibilityMode"] = lab ? "mdReporting" : key.StartsWith("mdAffected", StringComparison.Ordinal) ? "mdAffectedOwner" : owner ? "mdOwner" : "mdEbl",
            ["dueInDays"] = lab ? key == "mdLabBelow" ? "14" : "1" : "10",
            ["dayBasis"] = key == "mdLabBelow" ? "calendar" : "business", ["timeZoneId"] = "Eastern Standard Time",
            ["effectiveFrom"] = key == "mdChild2022" ? "2022-07-01" : key == "mdChild2023" ? "2022-10-28" : "2024-01-01",
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
        if (key == "mdChild2022") d["effectiveTo"] = "2022-10-27";
        if (key == "mdChild2023") d["effectiveTo"] = "2023-12-31";
        return d;
    }
}
