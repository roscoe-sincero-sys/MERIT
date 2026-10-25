namespace Merit.Domain.Residence;

public enum ScopeOutcome
{
    InvestigationRequest,
    ReviewTask,
}

public sealed record ScopeProposal(PlaceRow Place, ScopeOutcome Outcome, int Order, string Reason);

/// <summary>
/// Address rule 11: investigations draw on every location active within the lookback window before the qualifying
/// collection date, not only the primary. Residences and child-care or school sites always get an investigation
/// request; other places get one when their hours per week meet the configured minimum, and a review task otherwise.
/// The primary residence comes first.
/// </summary>
public static class InvestigationScope
{
    public static IReadOnlyList<ScopeProposal> Propose(IEnumerable<PlaceRow> places, DateOnly qualifyingCollectedDate,
                                                       int lookbackDays, decimal minHoursPerWeek)
    {
        var windowStart = qualifyingCollectedDate.AddDays(-lookbackDays);
        var active = places.Where(p => p.Overlaps(windowStart, qualifyingCollectedDate))
            .GroupBy(p => p.LocationId)
            .Select(g => g.OrderByDescending(p => p.IsPrimary).ThenByDescending(p => p.StartDate).First());

        var list = new List<ScopeProposal>();
        foreach (var p in active)
        {
            if (p.Role == "residence")
            {
                list.Add(new ScopeProposal(p, ScopeOutcome.InvestigationRequest, p.IsPrimary ? 0 : 1,
                    p.IsPrimary ? "Primary residence." : "Other residence within the lookback window."));
            }
            else if (p.Role is "childCare" or "school")
            {
                list.Add(new ScopeProposal(p, ScopeOutcome.InvestigationRequest, 2, "Child-care or school site."));
            }
            else if (p.HoursPerWeek is decimal h && h >= minHoursPerWeek)
            {
                list.Add(new ScopeProposal(p, ScopeOutcome.InvestigationRequest, 3,
                    $"{h:0.#} hours a week meets the {minHoursPerWeek:0.#}-hour minimum."));
            }
            else
            {
                list.Add(new ScopeProposal(p, ScopeOutcome.ReviewTask, 4,
                    p.HoursPerWeek is null
                        ? "Hours per week not recorded; review whether it needs an investigation."
                        : $"{p.HoursPerWeek:0.#} hours a week is below the {minHoursPerWeek:0.#}-hour minimum."));
            }
        }

        return list.OrderBy(x => x.Order).ToList();
    }
}
