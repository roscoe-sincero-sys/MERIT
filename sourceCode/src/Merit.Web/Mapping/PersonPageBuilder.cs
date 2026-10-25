using Merit.Application.Data;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;

namespace Merit.Web.Mapping;

/// <summary>
/// Builds the person page's view models from the application read model. The page has the same sections for everyone;
/// the population only changes their order and which empty states appear (specification, Person page table).
/// </summary>
public sealed class PersonPageBuilder(IReferenceDataProvider reference, ConfigService config, ICurrentSession session)
{
    private static readonly Dictionary<string, int[]> Order = new(StringComparer.Ordinal)
    {
        // key: child, adult, pregnant
        ["relationships"] = [2, 6, 7],
        ["places"] = [3, 3, 4],
        ["results"] = [4, 4, 5],
        ["cases"] = [5, 5, 6],
        ["employment"] = [6, 2, 3],
        ["sources"] = [7, 7, 8],
        ["pregnancy"] = [8, 8, 2],
        ["timeline"] = [9, 9, 9],
    };

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["relationships"] = "Relationships",
        ["places"] = "Places",
        ["results"] = "Results",
        ["cases"] = "Cases and investigations",
        ["employment"] = "Employment",
        ["sources"] = "Exposure sources",
        ["pregnancy"] = "Pregnancy",
        ["timeline"] = "Timeline",
    };

    public async Task<PersonPageModel> BuildAsync(PersonPage page, Guid? analyteFilter, CancellationToken ct)
    {
        int column = page.CurrentPopulation switch { "adult" => 1, "pregnant" => 2, _ => 0 };
        var person = page.Header.Person;
        bool showPregnancy = !(person.Sex == "male" && page.Pregnancies.Count == 0);
        var sections = Order.Where(kv => kv.Key != "pregnancy" || showPregnancy)
            .Select(kv => new PersonSection(kv.Key, Titles[kv.Key], kv.Value[column]))
            .OrderBy(s => s.Order).ToList();

        bool canEdit = session.Has(Permissions.PersonEdit) && person.MergedIntoPersonId is null;
        var refData = await reference.GetAsync(ct);
        return new PersonPageModel(page, sections,
            Relationships(person.PersonId, page.Relationships, canEdit, page.CurrentPopulation),
            await PlacesAsync(person.PersonId, page.Places, canEdit, ct),
            await ResultsAsync(person.PersonId, page.Results, analyteFilter, ct),
            new CasesSectionModel(person.PersonId, page.Cases),
            new EmploymentModel(person.PersonId, page.Employment, canEdit),
            new SourcesModel(person.PersonId, page.ExposureSources, canEdit, refData.Choices("exposureSourceType")),
            new PregnancyModel(person.PersonId, page.Pregnancies, canEdit, showPregnancy, person.Sex == "male"),
            canEdit, session.Has(Permissions.ResultEdit), session.Has(Permissions.PersonViewIdentifiers));
    }

    public RelationshipsModel Relationships(Guid personId, IReadOnlyList<RelationshipRow> rows, bool canEdit, string population)
        => new(personId, rows, canEdit, population == "child" ? "No guardians recorded." : "No relationships recorded.");

    public async Task<PlacesModel> PlacesAsync(Guid personId, IReadOnlyList<PlaceListRow> rows, bool canEdit, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        return new PlacesModel(personId, rows, canEdit,
            refData.Lookups("locationRole").ToDictionary(l => l.Code, l => l.DisplayName, StringComparer.Ordinal),
            refData.Lookups("primaryReason").ToDictionary(l => l.Code, l => l.DisplayName, StringComparer.Ordinal));
    }

    /// <summary>Results table plus one trend chart per analyte, with the case and action levels as threshold lines.</summary>
    public async Task<ResultsSectionModel> ResultsAsync(Guid personId, IReadOnlyList<PersonResultRow> rows, Guid? analyteFilter,
                                                       CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        var shown = analyteFilter is null ? rows : rows.Where(r => r.AnalyteId == analyteFilter).ToList();
        var analytes = rows.Select(r => (r.AnalyteId, r.AnalyteName)).Distinct().ToList();
        var options = new List<SelectOption> { new(string.Empty, "All analytes", analyteFilter is null) };
        options.AddRange(analytes.Select(a => new SelectOption(a.AnalyteId.ToString(), a.AnalyteName, a.AnalyteId == analyteFilter)));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var allThresholds = await config.ThresholdsAsync(ct);
        var charts = new List<TrendChart>();
        foreach (var group in shown.Where(r => r.ValueCanonical is not null && r.ResultRowStatus == "valid")
                     .GroupBy(r => r.AnalyteSpecimenId))
        {
            var first = group.First();
            var pop = first.Population is "unknown" ? "child" : first.Population;
            var levels = allThresholds
                .Where(t => t.AnalyteSpecimenId == group.Key && t.IsActive && t.ThresholdType != "reportable"
                            && t.EffectiveFrom <= today && (t.EffectiveTo is null || t.EffectiveTo >= today)
                            && (t.Population == "all" || t.Population == pop) && t.SampleType is null)
                .Select(t => ((t.ActionName ?? (t.ThresholdType == "case" ? "Case level" : "Action level")) + $" {t.LowerValue:0.##}", t.LowerValue))
                .Distinct().ToList();
            var unit = refData.Specimen(group.Key)?.CanonicalUnit ?? first.UnitCanonical;
            charts.Add(new TrendChart(first.AnalyteCode, $"{first.AnalyteName} ({Options.Humanize(first.Specimen)})", unit,
                group.OrderBy(r => r.CollectedDate).Select(r => new ChartPoint(r.CollectedDate, r.ValueCanonical!.Value, r.SampleType, r.Operator)).ToList(),
                levels));
        }

        return new ResultsSectionModel(personId, shown, analyteFilter, options, charts, session.Has(Permissions.ResultEdit));
    }
}
