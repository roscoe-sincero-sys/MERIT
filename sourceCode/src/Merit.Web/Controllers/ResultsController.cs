using System.Text.Json;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>
/// Result entry (htmx): fields cascade from the analyte lookup, one partial per change, so users only see what applies;
/// on save the form shows the canonical value and whether any threshold was crossed, from the server rules.
/// </summary>
public sealed class ResultsController(IClock clock, ResultService results, PersonService people, IReferenceDataProvider reference, LookupService lookup) : Controller
{
    [HttpGet("people/{personId:guid}/results/new")]
    [Authorize(Policy = Permissions.ResultEdit)]
    public async Task<IActionResult> New(Guid personId, CancellationToken ct)
    {
        var header = await people.GetHeaderAsync(personId, ct);
        var form = new ResultFormModel
        {
            PersonId = personId,
            PersonName = $"{header.Person.FirstName} {header.Person.LastName}".Trim(),
            CollectedDate = clock.Today,
        };
        return View(await FormAsync(form, ct));
    }

    /// <summary>The cascading part of the form: specimens for the analyte, then units, sample type, speciation, creatinine.</summary>
    [HttpGet("results/fields")]
    [Authorize(Policy = Permissions.ResultEdit)]
    public async Task<IActionResult> Fields(Guid? analyteId, Guid? analyteSpecimenId, string? unit, string? sampleType,
                                            string? speciation, decimal? creatinine, CancellationToken ct)
        => PartialView("_ResultFields", await FieldsAsync(analyteId, analyteSpecimenId, unit, sampleType, speciation, creatinine, ct));

    [HttpPost("people/{personId:guid}/results")]
    [Authorize(Policy = Permissions.ResultEdit)]
    public async Task<IActionResult> Save(Guid personId, ResultFormModel form, CancellationToken ct)
    {
        form.PersonId = personId;
        try
        {
            var summary = await results.SaveManualAsync(new ResultEntryInput(personId, form.AnalyteId, form.AnalyteSpecimenId, form.ValueText,
                form.Unit, form.SampleType, form.Speciation, form.Creatinine, form.CollectedDate, form.AnalyzedDate, form.ReportedDate,
                form.TestMethod, form.LabOrganizationId, form.OrderingProviderId, form.AccessionNumber, form.ResultStatus,
                form.ReasonForTest, form.PayerType), ct);
            Response.Announce("Result saved. " + summary.CaseOutcome);
            var model = new ResultSavedModel(personId, summary);
            return Request.IsHtmx() ? PartialView("_SaveSummary", model) : View("Saved", model);
        }
        catch (ValidationException ex)
        {
            var refreshed = await FormAsync(form, ct);
            if (Request.IsHtmx())
            {
                return this.Invalid("_ResultForm", refreshed, ex);
            }

            ModelState.AddErrors(ex);
            return View("New", refreshed);
        }
    }

    [HttpGet("results/{id:guid}")]
    [Authorize(Policy = Permissions.ResultView)]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var row = await results.GetAsync(id, ct);
        var snapshot = new List<KeyValuePair<string, string?>>();
        try
        {
            using var doc = JsonDocument.Parse(row.ReportedSnapshotJson);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                snapshot.Add(new KeyValuePair<string, string?>(p.Name, p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString()));
            }
        }
        catch (JsonException)
        {
            snapshot.Add(new KeyValuePair<string, string?>("raw", row.ReportedSnapshotJson));
        }

        return View(new ResultDetailModel(row, snapshot));
    }

    private async Task<ResultFormModel> FormAsync(ResultFormModel form, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        form.Analytes = new[] { new SelectOption(string.Empty, "- Select -", form.AnalyteId is null) }
            .Concat(refData.ActiveAnalytes.Select(a => new SelectOption(a.AnalyteId.ToString(), a.DisplayName, a.AnalyteId == form.AnalyteId)))
            .ToList();
        form.Methods = refData.Choices("testMethod", form.TestMethod, includeBlank: true);
        form.Reasons = refData.Choices("reasonForTest", form.ReasonForTest, includeBlank: true);
        form.Payers = refData.Choices("payerType", form.PayerType, includeBlank: true);
        form.Fields = await FieldsAsync(form.AnalyteId, form.AnalyteSpecimenId, form.Unit, form.SampleType, form.Speciation, form.Creatinine, ct);
        var (lab, provider) = await lookup.ResultSelectionsAsync(form.LabOrganizationId, form.OrderingProviderId, ct);
        form.LabOrganizationId = lab is null ? null : form.LabOrganizationId;
        form.LabLabel = lab?.Label;
        form.OrderingProviderId = provider is null ? null : form.OrderingProviderId;
        form.ProviderLabel = provider?.Label;
        if (string.IsNullOrEmpty(form.PersonName))
        {
            var h = await people.GetHeaderAsync(form.PersonId, ct);
            form.PersonName = $"{h.Person.FirstName} {h.Person.LastName}".Trim();
        }

        return form;
    }

    private async Task<ResultFieldsModel> FieldsAsync(Guid? analyteId, Guid? analyteSpecimenId, string? unit, string? sampleType,
                                                      string? speciation, decimal? creatinine, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        var model = new ResultFieldsModel { CreatinineCleared = creatinine is not null };
        if (analyteId is null)
        {
            return model;
        }

        var specimens = refData.SpecimensFor(analyteId.Value).ToList();
        var chosen = specimens.FirstOrDefault(s => s.AnalyteSpecimenId == analyteSpecimenId) ?? specimens.FirstOrDefault();
        model.Specimens = specimens.Select(s => new SelectOption(s.AnalyteSpecimenId.ToString(), Options.Humanize(s.Specimen),
            s.AnalyteSpecimenId == chosen?.AnalyteSpecimenId)).ToList();
        if (chosen is null)
        {
            return model;
        }

        model.CanonicalUnit = chosen.CanonicalUnit;
        model.Units = refData.UnitsFor(chosen.AnalyteSpecimenId)
            .Select(u => new SelectOption(u.UnitText, u.UnitText, u.UnitText == (unit ?? chosen.CanonicalUnit))).ToList();
        model.UsesSampleType = chosen.UsesSampleType;
        model.SampleTypes = refData.Choices("sampleType", sampleType ?? "unknown");
        model.Speciations = chosen.SpeciationList.Select(s => new SelectOption(s, Options.Humanize(s), s == speciation)).ToList();
        model.RequiresCreatinine = chosen.RequiresCreatinine;
        model.Creatinine = chosen.RequiresCreatinine ? creatinine : null;
        model.CreatinineCleared = !chosen.RequiresCreatinine && creatinine is not null;
        return model;
    }
}
