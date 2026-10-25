namespace Merit.Domain.Population;

public enum PopulationKind
{
    Unknown,
    Child,
    Adult,
    Pregnant,
}

public sealed record PregnancyEpisode(DateOnly StartDate, DateOnly? ExpectedEndDate, DateOnly? ActualEndDate)
{
    /// <summary>
    /// An episode is open from its start through the actual end; with no actual end, through the expected end; with
    /// neither, for the configured maximum length (default 42 weeks).
    /// </summary>
    public DateOnly OpenThrough(int maxWeeks)
        => ActualEndDate ?? ExpectedEndDate ?? StartDate.AddDays(maxWeeks * 7);

    public bool IsOpenOn(DateOnly date, int maxWeeks) => date >= StartDate && date <= OpenThrough(maxWeeks);
}

public sealed record PopulationResult(PopulationKind Population, int? AgeMonths);

/// <summary>
/// Population is derived per result from date of birth, collection date and pregnancy episodes -- never set with a
/// checkbox (design principle 1). Rules, in order: no DOB, unknown; open pregnancy on the collection date, pregnant;
/// age below the configured adult cutoff, child; otherwise adult.
/// </summary>
public static class PopulationRules
{
    public static PopulationResult Derive(DateOnly? dob, DateOnly collectedDate, IEnumerable<PregnancyEpisode> episodes,
                                          int adultAgeCutoffYears, int pregnancyMaxWeeks)
    {
        if (dob is null)
        {
            return new PopulationResult(PopulationKind.Unknown, null);
        }

        int months = AgeInMonths(dob.Value, collectedDate);
        if (episodes.Any(e => e.IsOpenOn(collectedDate, pregnancyMaxWeeks)))
        {
            return new PopulationResult(PopulationKind.Pregnant, months);
        }

        return months < adultAgeCutoffYears * 12
            ? new PopulationResult(PopulationKind.Child, months)
            : new PopulationResult(PopulationKind.Adult, months);
    }

    /// <summary>Whole months, counting a month once its day has been reached.</summary>
    public static int AgeInMonths(DateOnly dob, DateOnly asOf)
    {
        int months = ((asOf.Year - dob.Year) * 12) + asOf.Month - dob.Month;
        if (asOf.Day < dob.Day)
        {
            months--;
        }

        return Math.Max(months, 0);
    }

    public static int AgeInYears(DateOnly dob, DateOnly asOf) => AgeInMonths(dob, asOf) / 12;

    public static string ToCode(this PopulationKind p) => p switch
    {
        PopulationKind.Child => "child",
        PopulationKind.Adult => "adult",
        PopulationKind.Pregnant => "pregnant",
        _ => "unknown",
    };

    public static PopulationKind Parse(string? code) => code switch
    {
        "child" => PopulationKind.Child,
        "adult" => PopulationKind.Adult,
        "pregnant" => PopulationKind.Pregnant,
        _ => PopulationKind.Unknown,
    };
}
