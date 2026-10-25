using Merit.Application.Data;
using Merit.Application.Reference;
using Merit.Domain.Classification;
using Merit.Domain.Ids;
using Merit.Domain.Population;
using Merit.Domain.Residence;
using Merit.Domain.Results;
using Merit.Domain.Thresholds;

namespace Merit.Application.Services;

public sealed record ClassificationOutcome(
    PopulationKind Population,
    int? AgeMonths,
    ClassificationDecision Decision,
    Guid? ExposureCaseId,
    string? CaseNumber,
    string? OwningTenantName,
    string? ClassificationBefore,
    IReadOnlyList<string> TasksCreated);

/// <summary>
/// Runs intake normalization steps 6 and 7 for a saved result -- population from the matched person, comparison with
/// the thresholds in force on the collection date, and the case rules -- and applies the decision: open a case or
/// attach to the open one, and create the tasks the rules propose. Used by manual entry, by intake in the worker, and
/// by reruns after a DOB or pregnancy change, so both front ends and the worker share one copy of every rule.
/// </summary>
public sealed class ClassificationService(
    IResultData results,
    ICaseData cases,
    IWorkData work,
    IPersonData persons,
    IReferenceDataProvider reference,
    IThresholdProvider thresholds,
    ISettingsProvider settings)
{
    public async Task<ClassificationOutcome> ClassifyAsync(Guid labResultId, Guid personId, Guid analyteSpecimenId,
                                                           ClassifiableResult result, CancellationToken ct = default)
    {
        var refData = await reference.GetAsync(ct).ConfigureAwait(false);
        var config = await settings.GetAsync(ct).ConfigureAwait(false);
        var specimen = refData.Specimen(analyteSpecimenId) ?? throw new InvalidOperationException("Unknown analyte specimen.");
        var analyte = refData.Analyte(specimen.AnalyteId) ?? throw new InvalidOperationException("Unknown analyte.");

        var ctx = await results.GetClassificationContextAsync(personId, analyte.AnalyteId, ct).ConfigureAwait(false);
        var episodes = ctx.Episodes.Select(e => new PregnancyEpisode(e.StartDate, e.ExpectedEndDate, e.ActualEndDate)).ToList();
        var population = PopulationRules.Derive(ctx.Person?.Dob, result.CollectedDate, episodes, config.AdultAgeCutoffYears,
            config.PregnancyMaxWeeks);

        var rules = await thresholds.GetRulesAsync(analyteSpecimenId, result.CollectedDate, ct).ConfigureAwait(false);
        var priors = ctx.PriorResults
            .Where(p => p.LabResultId != labResultId && p.AnalyteSpecimenId == analyteSpecimenId)
            .Select(p => new ClassifiableResult(p.LabResultId, p.CollectedDate, p.SampleType, p.TestMethod, p.Speciation,
                ResultNormalizer.ParseOperator(p.Operator), p.ValueCanonical, true))
            .ToList();
        var openCase = ctx.OpenCase is null
            ? null
            : new OpenCaseInfo(ctx.OpenCase.ExposureCaseId, CaseClassifier.ParseClassification(ctx.OpenCase.ComputedClassification),
                ctx.OpenCase.HighestValue);

        var decision = CaseClassifier.Classify(new ClassificationInput(analyte.Code, specimen.Specimen, population.Population,
            population.AgeMonths, result, priors, rules, openCase, config.Classification));

        await results.UpdateDerivedAsync(labResultId, personId, population.AgeMonths, population.Population.ToCode(),
            decision.IsIndeterminate, ct).ConfigureAwait(false);

        Guid? caseId = ctx.OpenCase?.ExposureCaseId;
        string? caseNumber = ctx.OpenCase?.CaseNumber;
        string? owner = null;
        string? before = ctx.OpenCase?.Classification;
        var created = new List<string>();

        if (decision.Action == CaseAction.OpenCase)
        {
            caseId = TimeOrderedId.New();
            var opened = await cases.OpenCaseAsync(caseId.Value, personId, analyte.AnalyteId, population.Population.ToCode(),
                decision.Classification.ToCode(), labResultId, CaseClassifier.WorkflowTemplate(population.Population),
                DateOnly.FromDateTime(DateTime.UtcNow), ct).ConfigureAwait(false);
            caseNumber = opened.CaseNumber;
            owner = opened.OwningTenantName;
            await cases.CreateFollowUpTasksAsync(caseId.Value, config.AdultAgeCutoffYears, ct).ConfigureAwait(false);
            created.AddRange(await ProposeInvestigationsAsync(personId, caseId.Value, result.CollectedDate, config, ct).ConfigureAwait(false));
        }
        else if (decision.Action == CaseAction.AttachToCase && caseId is not null && result.IsFinal)
        {
            var attached = await cases.AttachResultAsync(caseId.Value, labResultId, decision.Classification.ToCode(),
                decision.IsHighest, ct).ConfigureAwait(false);
            before = attached.ClassificationBefore;
        }

        foreach (var task in decision.Tasks)
        {
            DateOnly? due = task.DueInDays is int days ? result.CollectedDate.AddDays(days) : null;
            await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), task.QueueCode, task.Title, task.Detail, personId,
                caseId, labResultId, null, null, task.IsUrgent, due), ct).ConfigureAwait(false);
            created.Add(task.Title);
        }

        return new ClassificationOutcome(population.Population, population.AgeMonths, decision, caseId, caseNumber, owner,
            before, created);
    }

    /// <summary>
    /// Re-runs classification for a person's results after a DOB change or a pregnancy episode change: populations are
    /// re-derived, and an open case takes the strongest classification its results now support. Closed cases and
    /// classifications set by staff override are never changed. Returns what changed, for the user to see.
    /// </summary>
    public async Task<IReadOnlyList<string>> RerunForPersonAsync(Guid personId, CancellationToken ct = default)
    {
        var refData = await reference.GetAsync(ct).ConfigureAwait(false);
        var config = await settings.GetAsync(ct).ConfigureAwait(false);
        var all = await persons.GetResultsAsync(personId, null, ct).ConfigureAwait(false);
        var changes = new List<string>();

        foreach (var analyteGroup in all.Where(r => r.ResultRowStatus == "valid").GroupBy(r => r.AnalyteId))
        {
            var ctx = await results.GetClassificationContextAsync(personId, analyteGroup.Key, ct).ConfigureAwait(false);
            var episodes = ctx.Episodes.Select(e => new PregnancyEpisode(e.StartDate, e.ExpectedEndDate, e.ActualEndDate)).ToList();
            foreach (var r in analyteGroup)
            {
                var pop = PopulationRules.Derive(ctx.Person?.Dob, r.CollectedDate, episodes, config.AdultAgeCutoffYears,
                    config.PregnancyMaxWeeks);
                if (pop.Population.ToCode() != r.Population)
                {
                    await results.UpdateDerivedAsync(r.LabResultId, personId, pop.AgeMonths, pop.Population.ToCode(),
                        r.IsIndeterminate, ct).ConfigureAwait(false);
                    changes.Add($"{r.AnalyteCode} result of {r.CollectedDate:yyyy-MM-dd}: population {r.Population} -> {pop.Population.ToCode()}.");
                }
            }

            if (ctx.OpenCase is null || ctx.OpenCase.OverrideReason is not null)
            {
                continue;
            }

            // Strongest classification the case's results support under the current populations.
            var strongest = CaseClassification.NotACase;
            Guid? strongestResult = null;
            foreach (var r in analyteGroup.Where(r => r.ResultStatus is "final" or "corrected").OrderBy(r => r.CollectedDate))
            {
                var pop = PopulationRules.Derive(ctx.Person?.Dob, r.CollectedDate, episodes, config.AdultAgeCutoffYears,
                    config.PregnancyMaxWeeks);
                var rules = await thresholds.GetRulesAsync(r.AnalyteSpecimenId, r.CollectedDate, ct).ConfigureAwait(false);
                var cr = new ClassifiableResult(r.LabResultId, r.CollectedDate, r.SampleType, r.TestMethod, r.Speciation,
                    ResultNormalizer.ParseOperator(r.Operator), r.ValueCanonical, true);
                var priors = analyteGroup.Where(p => p.LabResultId != r.LabResultId && p.CollectedDate <= r.CollectedDate)
                    .Select(p => new ClassifiableResult(p.LabResultId, p.CollectedDate, p.SampleType, p.TestMethod, p.Speciation,
                        ResultNormalizer.ParseOperator(p.Operator), p.ValueCanonical, true)).ToList();
                var d = CaseClassifier.Classify(new ClassificationInput(r.AnalyteCode, r.Specimen, pop.Population, pop.AgeMonths,
                    cr, priors, rules, null, config.Classification));
                if (d.CrossedCaseLevel && d.Classification > strongest)
                {
                    strongest = d.Classification;
                    strongestResult = r.LabResultId;
                }
            }

            var current = CaseClassifier.ParseClassification(ctx.OpenCase.ComputedClassification);
            if (strongest > current && strongestResult is not null)
            {
                await cases.AttachResultAsync(ctx.OpenCase.ExposureCaseId, strongestResult.Value, strongest.ToCode(), false, ct)
                    .ConfigureAwait(false);
                changes.Add($"Case {ctx.OpenCase.CaseNumber}: classification {current.ToCode()} -> {strongest.ToCode()}.");
            }
            else if (strongest < current)
            {
                changes.Add($"Case {ctx.OpenCase.CaseNumber}: the results no longer support {current.ToCode()}; the open case keeps it. Review the classification.");
            }
        }

        _ = refData;
        return changes;
    }

    /// <summary>
    /// Address rule 11: investigation requests for residences and child-care or school sites in the lookback window,
    /// and a request or review task for other places by the hours-per-week minimum. Proposals are tasks; staff open the
    /// investigation, reusing any open one of the same place.
    /// </summary>
    private async Task<IReadOnlyList<string>> ProposeInvestigationsAsync(Guid personId, Guid caseId, DateOnly collected,
                                                                        JurisdictionSettings config, CancellationToken ct)
    {
        if (config.InvestigationLookbackDays is not int lookback || config.SiteInvestigationMinHours is not decimal minHours)
        {
            await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "investigationRequest",
                "Decide which places to investigate",
                "The investigation lookback and site minimum-hours settings are not set, so places were not proposed automatically.",
                personId, caseId, null, null, null, false, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14)), ct).ConfigureAwait(false);
            return ["Decide which places to investigate"];
        }

        var places = await persons.GetPlacesAsync(personId, ct).ConfigureAwait(false);
        var rows = places.Select(PlaceService.ToPlaceRow).ToList();
        var titles = new List<string>();
        foreach (var p in InvestigationScope.Propose(rows, collected, lookback, minHours))
        {
            string title = p.Outcome == ScopeOutcome.InvestigationRequest
                ? $"Investigate {p.Place.AddressLabel}"
                : $"Review whether to investigate {p.Place.AddressLabel}";
            await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(),
                p.Outcome == ScopeOutcome.InvestigationRequest ? "investigationRequest" : "siteReview", title, p.Reason,
                personId, caseId, null, null, null, false, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14)), ct).ConfigureAwait(false);
            titles.Add(title);
        }

        return titles;
    }
}
