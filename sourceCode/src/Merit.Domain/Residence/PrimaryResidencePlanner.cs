namespace Merit.Domain.Residence;

/// <summary>One person-location row as the rules see it.</summary>
public sealed record PlaceRow(
    Guid PersonLocationId,
    Guid LocationId,
    Guid? OrganizationSiteId,
    string Role,
    bool IsPrimary,
    string? PrimaryReason,
    string? ScheduleType,
    decimal? HoursPerWeek,
    string? ScheduleNote,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Source,
    DateOnly? VerifiedDate,
    string? RowVersionHex,
    string? CountyFips = null,
    string? CountyName = null,
    string? AddressLabel = null)
{
    public bool IsResidence => Role == "residence";

    public bool Covers(DateOnly date) => StartDate <= date && (EndDate is null || EndDate >= date);

    public bool Overlaps(DateOnly start, DateOnly? end)
        => StartDate <= (end ?? DateOnly.MaxValue) && start <= (EndDate ?? DateOnly.MaxValue);
}

public enum ChangeKind
{
    Insert,
    Update,
}

public sealed record PlaceChange(ChangeKind Kind, PlaceRow Row, string Description);

public sealed record PlacePlan(
    IReadOnlyList<PlaceChange> Changes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? CountyBefore,
    string? CountyAfter)
{
    public bool IsValid => Errors.Count == 0;

    public bool CountyChanges => !string.Equals(CountyBefore, CountyAfter, StringComparison.Ordinal);
}

/// <summary>
/// The address and primary residence rules, written once and used by both the preview a user sees and the save:
/// one primary at a time (1), no gaps once known (2), a reason on overlap (3), and changing the primary splits rows (12)
/// -- the old primary row ends the day before the new start and, when the person still lives there, a new non-primary
/// row continues from that date with the same schedule, so history stays exact. dbo.uspSavePersonLocations enforces
/// rules 1-3 again under row locks.
/// </summary>
public static class PrimaryResidencePlanner
{
    /// <summary>Plans "Set as primary" for an existing residence row from <paramref name="startDate"/>.</summary>
    public static PlacePlan PlanSetPrimary(IReadOnlyList<PlaceRow> rows, Guid targetRowId, DateOnly startDate,
                                           string? reason, Func<Guid> newId, bool isAdult = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var target = rows.FirstOrDefault(r => r.PersonLocationId == targetRowId);
        if (target is null || !target.IsResidence)
        {
            return new PlacePlan([], ["Only a residence can be the primary residence."], [], null, null);
        }

        if (target.EndDate is not null && target.EndDate < startDate)
        {
            return new PlacePlan([], ["The new primary residence has ended before that start date."], [], null, null);
        }

        if (startDate < target.StartDate)
        {
            errors.Add($"The person did not live there yet on {startDate:yyyy-MM-dd}; the residence starts {target.StartDate:yyyy-MM-dd}.");
        }

        var working = rows.ToDictionary(r => r.PersonLocationId);
        var changes = new List<PlaceChange>();

        // Old primaries in force on or after the start date end the day before it.
        foreach (var p in rows.Where(r => r.IsResidence && r.IsPrimary && r.PersonLocationId != targetRowId
                                          && (r.EndDate is null || r.EndDate >= startDate)))
        {
            if (p.StartDate >= startDate)
            {
                var demoted = p with { IsPrimary = false, PrimaryReason = null };
                working[p.PersonLocationId] = demoted;
                changes.Add(new PlaceChange(ChangeKind.Update, demoted, $"{Label(p)} is no longer primary."));
                continue;
            }

            var ended = p with { EndDate = startDate.AddDays(-1) };
            working[p.PersonLocationId] = ended;
            changes.Add(new PlaceChange(ChangeKind.Update, ended,
                $"{Label(p)}: primary row ends {startDate.AddDays(-1):yyyy-MM-dd}."));

            var continuation = p with
            {
                PersonLocationId = newId(),
                IsPrimary = false,
                PrimaryReason = null,
                StartDate = startDate,
                RowVersionHex = null,
            };
            working[continuation.PersonLocationId] = continuation;
            changes.Add(new PlaceChange(ChangeKind.Insert, continuation,
                $"{Label(p)}: the person still lives there, so a non-primary row continues from {startDate:yyyy-MM-dd}."));
        }

        // The target becomes primary from the start date, splitting its own row when it began earlier.
        if (target.StartDate < startDate)
        {
            var before = target with { EndDate = startDate.AddDays(-1), IsPrimary = false };
            working[target.PersonLocationId] = before;
            changes.Add(new PlaceChange(ChangeKind.Update, before,
                $"{Label(target)}: the earlier row ends {startDate.AddDays(-1):yyyy-MM-dd}."));
            var primary = target with
            {
                PersonLocationId = newId(),
                IsPrimary = true,
                PrimaryReason = reason,
                StartDate = startDate,
                RowVersionHex = null,
            };
            working[primary.PersonLocationId] = primary;
            changes.Add(new PlaceChange(ChangeKind.Insert, primary, $"{Label(target)} is primary from {startDate:yyyy-MM-dd}."));
        }
        else
        {
            var primary = target with { IsPrimary = true, PrimaryReason = reason };
            working[target.PersonLocationId] = primary;
            changes.Add(new PlaceChange(ChangeKind.Update, primary, $"{Label(target)} is primary from {target.StartDate:yyyy-MM-dd}."));
        }

        var after = working.Values.ToList();
        Validate(after, errors, warnings, isAdult);
        return new PlacePlan(changes, errors, warnings, CountyOn(rows, startDate), CountyOn(after, startDate));
    }

    /// <summary>
    /// Plans adding a place. A residence that overlaps another residence cannot be saved without a primary choice
    /// (rule 2): <paramref name="newIsPrimary"/> true makes the new residence primary (splitting the old primary),
    /// false keeps the existing primary, which then needs <paramref name="reason"/> when it has none. A residence
    /// with nothing to overlap becomes primary by itself.
    /// </summary>
    public static PlacePlan PlanAddPlace(IReadOnlyList<PlaceRow> rows, PlaceRow newRow, bool? newIsPrimary, string? reason,
                                         Func<Guid> newId, bool isAdult = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (newRow.EndDate is not null && newRow.EndDate < newRow.StartDate)
        {
            return new PlacePlan([], ["The end date is before the start date."], [], null, null);
        }

        if (!newRow.IsResidence)
        {
            var nonResidence = newRow with { IsPrimary = false, PrimaryReason = null };
            return new PlacePlan([new PlaceChange(ChangeKind.Insert, nonResidence, $"{Label(newRow)} added.")], [], [],
                CountyOn(rows, newRow.StartDate), CountyOn(rows, newRow.StartDate));
        }

        var overlapping = rows.Where(r => r.IsResidence && r.Overlaps(newRow.StartDate, newRow.EndDate)).ToList();
        if (overlapping.Count == 0)
        {
            var only = newRow with { IsPrimary = true, PrimaryReason = reason ?? newRow.PrimaryReason };
            var plan = new List<PlaceChange> { new(ChangeKind.Insert, only, $"{Label(newRow)} added as the primary residence.") };
            var after0 = rows.Append(only).ToList();
            Validate(after0, errors, warnings, isAdult);
            return new PlacePlan(plan, errors, warnings, CountyOn(rows, newRow.StartDate), CountyOn(after0, newRow.StartDate));
        }

        if (newIsPrimary is null)
        {
            return new PlacePlan([],
                [$"This residence overlaps {string.Join(", ", overlapping.Select(Label))}. Choose which is the primary residence."],
                [], null, null);
        }

        if (newIsPrimary == true)
        {
            var inserted = newRow with { IsPrimary = false, PrimaryReason = null };
            var withNew = rows.Append(inserted).ToList();
            var sp = PlanSetPrimary(withNew, inserted.PersonLocationId, newRow.StartDate, reason, newId, isAdult);
            var changes = new List<PlaceChange> { new(ChangeKind.Insert, inserted, $"{Label(newRow)} added.") };

            // The set-primary plan updates the row just inserted; fold that update into the insert.
            foreach (var c in sp.Changes)
            {
                if (c.Row.PersonLocationId == inserted.PersonLocationId)
                {
                    changes[0] = new PlaceChange(ChangeKind.Insert, c.Row, c.Description);
                }
                else
                {
                    changes.Add(c);
                }
            }

            return new PlacePlan(changes, sp.Errors, sp.Warnings, CountyOn(rows, newRow.StartDate), sp.CountyAfter);
        }

        var secondary = newRow with { IsPrimary = false, PrimaryReason = null };
        var result = new List<PlaceChange> { new(ChangeKind.Insert, secondary, $"{Label(newRow)} added; not primary.") };
        var working = rows.ToDictionary(r => r.PersonLocationId);
        working[secondary.PersonLocationId] = secondary;
        foreach (var p in overlapping.Where(o => o.IsPrimary && o.PrimaryReason is null))
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                errors.Add($"{Label(p)} stays primary while the residences overlap, so it needs a reason.");
                break;
            }

            var withReason = p with { PrimaryReason = reason };
            working[p.PersonLocationId] = withReason;
            result.Add(new PlaceChange(ChangeKind.Update, withReason, $"{Label(p)} stays primary: reason recorded."));
        }

        var after = working.Values.ToList();
        Validate(after, errors, warnings, isAdult);
        return new PlacePlan(result, errors, warnings, CountyOn(rows, newRow.StartDate), CountyOn(after, newRow.StartDate));
    }

    /// <summary>Rules 1-3 over the rows as they would stand, plus the adult two-residence warning (rule 10).</summary>
    public static void Validate(IReadOnlyList<PlaceRow> rows, List<string> errors, List<string> warnings, bool isAdult)
    {
        var residences = rows.Where(r => r.IsResidence).ToList();
        var primaries = residences.Where(r => r.IsPrimary).ToList();

        for (int i = 0; i < primaries.Count; i++)
        {
            for (int j = i + 1; j < primaries.Count; j++)
            {
                if (primaries[i].Overlaps(primaries[j].StartDate, primaries[j].EndDate))
                {
                    errors.Add("Two primary residences would overlap; a person has one primary residence on any date.");
                    i = primaries.Count;
                    break;
                }
            }
        }

        foreach (var p in primaries.Where(p => p.PrimaryReason is null))
        {
            if (residences.Any(o => o.PersonLocationId != p.PersonLocationId && o.Overlaps(p.StartDate, p.EndDate)))
            {
                errors.Add($"{Label(p)} overlaps another residence, so the primary needs a reason.");
                break;
            }
        }

        // A gap can only begin at a residence's start or the day after a primary ends.
        var candidates = residences.Select(r => r.StartDate)
            .Concat(primaries.Where(p => p.EndDate is not null && p.EndDate < DateOnly.MaxValue).Select(p => p.EndDate!.Value.AddDays(1)));
        foreach (var d in candidates.Distinct().Order())
        {
            if (residences.Any(r => r.Covers(d)) && !primaries.Any(p => p.Covers(d)))
            {
                errors.Add($"On {d:yyyy-MM-dd} the person would have a residence but no primary residence.");
                break;
            }
        }

        if (isAdult)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (residences.Count(r => r.Covers(today)) > 1)
            {
                warnings.Add("This adult has two current residences. That is allowed (seasonal homes, shelters); check it is right.");
            }
        }
    }

    public static string? CountyOn(IEnumerable<PlaceRow> rows, DateOnly date)
    {
        var p = rows.Where(r => r.IsResidence && r.IsPrimary && r.Covers(date)).OrderByDescending(r => r.StartDate).FirstOrDefault();
        return p is null ? null : p.CountyName ?? p.CountyFips;
    }

    private static string Label(PlaceRow r) => r.AddressLabel ?? "The residence";
}
