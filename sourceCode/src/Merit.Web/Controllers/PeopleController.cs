using System.Text.Json;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Mapping;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>
/// The person page (htmx): each section is a partial that reloads after its own edit; the trend chart is server-rendered
/// SVG. Every section URL also renders as a full page on its own, so anything htmx pushes into history works.
/// </summary>
[Route("people")]
public sealed class PeopleController(IClock clock, PersonService people, PersonPageBuilder builder, IReferenceDataProvider reference, LookupService lookup) : Controller
{
    // ------------------------------------------------------------------------------------------ person
    [HttpGet("new")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> New(CancellationToken ct) => View(await FormAsync(new PersonFormModel(), ct));

    [HttpPost("new")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> New(PersonFormModel form, CancellationToken ct)
    {
        try
        {
            var outcome = await people.SaveAsync(ToInput(form), ct);
            return RedirectToAction(nameof(Details), new { id = outcome.PersonId });
        }
        catch (ValidationException ex)
        {
            AddErrors(ex);
            return View(await FormAsync(form, ct));
        }
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Details(Guid id, Guid? analyteId, CancellationToken ct)
        => View("Details", await builder.BuildAsync(await people.GetPageAsync(id, ct), analyteId, ct));

    [HttpGet("{id:guid}/edit")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var header = await people.GetHeaderAsync(id, ct);
        var h = header.Person;
        var form = new PersonFormModel
        {
            PersonId = id, LastName = h.LastName, FirstName = h.FirstName, MiddleName = h.MiddleName, Dob = h.Dob,
            DobVerified = h.DobVerified, Sex = h.Sex, Ethnicity = h.Ethnicity, PreferredLanguage = h.PreferredLanguage,
            DeceasedDate = h.DeceasedDate, RaceCodes = header.Races.Select(r => r.RaceCode).ToList(), RowVersion = Options.Hex(h.RowVersion),
        };
        return Request.IsPartial() ? PartialView("_PersonForm", await FormAsync(form, ct)) : View("New", await FormAsync(form, ct));
    }

    [HttpPost("{id:guid}/edit")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Edit(Guid id, PersonFormModel form, CancellationToken ct)
    {
        form.PersonId = id;
        try
        {
            var outcome = await people.SaveAsync(ToInput(form), ct);
            foreach (var note in outcome.Changes)
            {
                Response.Announce(note);
            }

            if (!Request.IsHtmx())
            {
                return RedirectToAction(nameof(Details), new { id });
            }

            Response.Headers["HX-Redirect"] = Url.Action(nameof(Details), new { id });
            return Ok();
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_PersonForm", await FormAsync(form, ct), ex);
        }
    }

    [HttpPost("{id:guid}/contacts")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> AddContact(Guid id, ContactFormModel form, CancellationToken ct)
    {
        await people.SaveContactMethodAsync(id, null, form.ContactType, form.ContactValue ?? string.Empty, form.IsPreferred, false, ct);
        return await HeaderPartialAsync(id, ct);
    }

    [HttpPost("{id:guid}/contacts/{contactId:guid}/remove")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> RemoveContact(Guid id, Guid contactId, CancellationToken ct)
    {
        var header = await people.GetHeaderAsync(id, ct);
        var c = header.ContactMethods.FirstOrDefault(x => x.PersonContactMethodId == contactId) ?? throw new NotFoundException();
        await people.SaveContactMethodAsync(id, contactId, c.ContactType, c.ContactValue, false, true, ct);
        return await HeaderPartialAsync(id, ct);
    }

    [HttpPost("{id:guid}/identifiers")]
    [Authorize(Policy = Permissions.PersonViewIdentifiers)]
    public async Task<IActionResult> AddIdentifier(Guid id, string identifierType, string identifierValue, string? assigningFacility,
                                                   CancellationToken ct)
    {
        await people.SaveIdentifierAsync(id, identifierType, identifierValue, assigningFacility, ct);
        return await HeaderPartialAsync(id, ct);
    }

    // ------------------------------------------------------------------------------------------ sections
    [HttpGet("{id:guid}/header")]
    [Authorize(Policy = Permissions.PersonView)]
    public Task<IActionResult> Header(Guid id, CancellationToken ct) => HeaderPartialAsync(id, ct);

    [HttpGet("{id:guid}/relationships")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Relationships(Guid id, CancellationToken ct)
        => await SectionAsync(id, "_Relationships", m => m.Relationships, ct);

    [HttpGet("{id:guid}/results")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Results(Guid id, Guid? analyteId, CancellationToken ct)
    {
        if (!Request.IsPartial())
        {
            return await Details(id, analyteId, ct);
        }

        var rows = await people.GetResultsAsync(id, null, ct);
        return PartialView("_Results", await builder.ResultsAsync(id, rows, analyteId, ct));
    }

    [HttpGet("{id:guid}/cases")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Cases(Guid id, CancellationToken ct)
        => await SectionAsync(id, "_Cases", m => m.Cases, ct);

    [HttpGet("{id:guid}/employment")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Employment(Guid id, CancellationToken ct)
        => await SectionAsync(id, "_Employment", m => m.Employment, ct);

    [HttpGet("{id:guid}/sources")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Sources(Guid id, CancellationToken ct)
        => await SectionAsync(id, "_Sources", m => m.Sources, ct);

    [HttpGet("{id:guid}/pregnancies")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Pregnancies(Guid id, CancellationToken ct)
        => await SectionAsync(id, "_Pregnancy", m => m.Pregnancy, ct);

    // ------------------------------------------------------------------------------------------ relationships
    [HttpGet("{id:guid}/relationships/add")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> AddRelationship(Guid id, CancellationToken ct)
        => PartialView("_RelationshipForm", await RelationshipFormAsync(new RelationshipFormModel { PersonId = id }, ct));

    [HttpPost("{id:guid}/relationships")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> AddRelationship(Guid id, RelationshipFormModel form, CancellationToken ct)
    {
        form.PersonId = id;
        try
        {
            await people.AddRelationshipAsync(new RelationshipInput(id, form.RelatedPersonId, form.NewLastName, form.NewFirstName,
                form.NewDob, null, form.Role, form.IsPrimaryContact, form.HasLegalCustody, null, form.Source), ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_RelationshipForm", await RelationshipFormAsync(form, ct), ex);
        }

        Response.Announce("Relationship added.");
        return await SectionAsync(id, "_Relationships", m => m.Relationships, ct, forcePartial: true);
    }

    [HttpPost("{id:guid}/relationships/{relationshipId:guid}/end")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> EndRelationship(Guid id, Guid relationshipId, CancellationToken ct)
    {
        await people.EndRelationshipAsync(id, relationshipId, clock.Today, ct);
        Response.Announce("Relationship ended.");
        return await SectionAsync(id, "_Relationships", m => m.Relationships, ct, forcePartial: true);
    }

    // ------------------------------------------------------------------------------------------ employment
    [HttpGet("{id:guid}/employment/add")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> AddEmployment(Guid id, CancellationToken ct)
        => PartialView("_EmploymentForm", await EmploymentFormAsync(new EmploymentFormModel { PersonId = id }, ct));

    [HttpGet("{id:guid}/employment/{employmentId:guid}/edit")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> EditEmployment(Guid id, Guid employmentId, CancellationToken ct)
    {
        var e = (await people.GetPageAsync(id, ct)).Employment.FirstOrDefault(x => x.EmploymentId == employmentId) ?? throw new NotFoundException();
        var ids = JsonSerializer.Deserialize<List<Dictionary<string, Guid>>>(e.AnalyteIdsJson) ?? [];
        return PartialView("_EmploymentForm", await EmploymentFormAsync(new EmploymentFormModel {
            PersonId = id, EmploymentId = employmentId, OrganizationId = e.OrganizationId, JobTitle = e.JobTitle,
            IndustryText = e.IndustryText, OccupationCode = e.OccupationCode, NaicsOverride = e.NaicsOverride,
            MetalTasks = e.MetalTasks, PpeNotes = e.PpeNotes, HoursPerWeek = e.HoursPerWeek,
            StartDate = e.StartDate, EndDate = e.EndDate, Source = e.Source, RowVersion = Options.Hex(e.RowVersion),
            AnalyteIds = ids.Select(x => x["analyteId"]).ToList()
        }, ct));
    }

    [HttpPost("{id:guid}/employment")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> AddEmployment(Guid id, EmploymentFormModel form, CancellationToken ct)
    {
        form.PersonId = id;
        AddressInput? worksite = string.IsNullOrWhiteSpace(form.WorksiteAddressLine1)
            ? null
            : new AddressInput(form.WorksiteAddressLine1, null, form.WorksiteCity, form.WorksiteState, form.WorksiteZip5,
                CountyFips: form.WorksiteCountyFips);
        try
        {
            await people.SaveEmploymentAsync(new EmploymentInput(form.EmploymentId, id, form.OrganizationId, form.NewOrganizationName, worksite, null,
                form.JobTitle, form.IndustryText, form.OccupationCode, form.NaicsOverride, form.MetalTasks, form.PpeNotes,
                form.HoursPerWeek, form.StartDate, form.EndDate, form.Source, form.AnalyteIds, Options.FromHex(form.RowVersion), form.Verify), ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_EmploymentForm", await EmploymentFormAsync(form, ct), ex);
        }

        Response.Announce("Job added.");
        return await SectionAsync(id, "_Employment", m => m.Employment, ct, forcePartial: true);
    }

    // ------------------------------------------------------------------------------------------ exposure sources
    [HttpGet("{id:guid}/sources/add")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> AddSource(Guid id, CancellationToken ct)
        => PartialView("_SourceForm", await SourceFormAsync(new SourceFormModel { PersonId = id }, ct));

    [HttpGet("{id:guid}/sources/{sourceId:guid}/edit")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> EditSource(Guid id, Guid sourceId, CancellationToken ct)
    {
        var s = (await people.GetPageAsync(id, ct)).ExposureSources.FirstOrDefault(x => x.ExposureSourceId == sourceId) ?? throw new NotFoundException();
        return PartialView("_SourceForm", await SourceFormAsync(new SourceFormModel {
            PersonId = id, ExposureSourceId = sourceId, SourceType = s.SourceType, Description = s.SourceDescription,
            ProductName = s.ProductName, ProductBrand = s.Brand, ProductCountry = s.ProductCountry,
            StartDate = s.StartDate, EndDate = s.EndDate, IsConfirmed = s.IsConfirmed, OriginalModifiedUtc = s.ModifiedDateUtc
        }, ct));
    }

    [HttpPost("{id:guid}/sources")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> AddSource(Guid id, SourceFormModel form, CancellationToken ct)
    {
        form.PersonId = id;
        try
        {
            await people.SaveExposureSourceAsync(new ExposureSourceInput(id, form.SourceType, form.Description, form.ProductName,
                form.ProductBrand, form.ProductCountry, form.StartDate, form.EndDate, form.IsConfirmed, form.ExposureSourceId, form.OriginalModifiedUtc), ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_SourceForm", await SourceFormAsync(form, ct), ex);
        }

        Response.Announce("Exposure source added.");
        return await SectionAsync(id, "_Sources", m => m.Sources, ct, forcePartial: true);
    }

    // ------------------------------------------------------------------------------------------ pregnancy
    [HttpGet("{id:guid}/pregnancies/add")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public IActionResult AddPregnancy(Guid id) => PartialView("_PregnancyForm", new PregnancyFormModel { PersonId = id });

    [HttpGet("{id:guid}/pregnancies/{episodeId:guid}/edit")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> EditPregnancy(Guid id, Guid episodeId, CancellationToken ct)
    {
        var e = (await people.GetPregnanciesAsync(id, ct)).FirstOrDefault(p => p.PregnancyEpisodeId == episodeId) ?? throw new NotFoundException();
        return PartialView("_PregnancyForm", new PregnancyFormModel
        {
            PersonId = id, PregnancyEpisodeId = episodeId, StartDate = e.StartDate, ExpectedEndDate = e.ExpectedEndDate,
            ActualEndDate = e.ActualEndDate, Outcome = e.Outcome, Source = e.Source, RowVersion = Options.Hex(e.RowVersion),
        });
    }

    [HttpPost("{id:guid}/pregnancies")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> SavePregnancy(Guid id, PregnancyFormModel form, CancellationToken ct)
    {
        form.PersonId = id;
        if (form.StartDate is null)
        {
            ModelState.AddModelError(nameof(form.StartDate), "Enter the start date.");
            return this.Invalid("_PregnancyForm", form);
        }

        try
        {
            var notes = await people.SavePregnancyAsync(new PregnancyInput(form.PregnancyEpisodeId, id, form.StartDate.Value,
                form.ExpectedEndDate, form.ActualEndDate, form.Outcome, form.Source, Options.FromHex(form.RowVersion)), ct);
            Response.Announce(notes.Count == 0 ? "Pregnancy episode saved." : string.Join(" ", notes));
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_PregnancyForm", form, ex);
        }

        // Population may have changed, so the whole page is reloaded.
        Response.Headers["HX-Redirect"] = Url.Action(nameof(Details), new { id });
        return Ok();
    }

    // ------------------------------------------------------------------------------------------ helpers
    private async Task<IActionResult> SectionAsync(Guid id, string partial, Func<PersonPageModel, object> pick, CancellationToken ct,
                                                   bool forcePartial = false)
    {
        var model = await builder.BuildAsync(await people.GetPageAsync(id, ct), null, ct);
        return forcePartial || Request.IsPartial() ? PartialView(partial, pick(model)) : View("Details", model);
    }

    private async Task<IActionResult> HeaderPartialAsync(Guid id, CancellationToken ct)
    {
        var model = await builder.BuildAsync(await people.GetPageAsync(id, ct), null, ct);
        return Request.IsHtmx() ? PartialView("_Header", model) : View("Details", model);
    }

    private async Task<PersonFormModel> FormAsync(PersonFormModel form, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        form.Races = refData.Lookups("race").Select(r => new SelectOption(r.Code, r.DisplayName, form.RaceCodes.Contains(r.Code))).ToList();
        form.Languages = refData.Choices("language", form.PreferredLanguage, includeBlank: true);
        return form;
    }

    private async Task<RelationshipFormModel> RelationshipFormAsync(RelationshipFormModel form, CancellationToken ct)
    {
        var selected = await lookup.SelectionAsync("person", form.RelatedPersonId, ct);
        form.RelatedPersonId = selected is null ? null : form.RelatedPersonId;
        form.RelatedPersonLabel = selected?.Label;
        form.Roles = (await reference.GetAsync(ct)).Choices("relationshipRole", form.Role);
        return form;
    }

    private async Task<EmploymentFormModel> EmploymentFormAsync(EmploymentFormModel form, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        var employer = await lookup.SelectionAsync("organization", form.OrganizationId, ct, "employer");
        form.OrganizationId = employer is null ? null : form.OrganizationId;
        form.OrganizationLabel = employer?.Label;
        form.Analytes = refData.Set.Analytes.Where(a => a.IsActive || form.AnalyteIds.Contains(a.AnalyteId)).Select(a => new SelectOption(a.AnalyteId.ToString(), a.DisplayName, form.AnalyteIds.Contains(a.AnalyteId))).ToList();
        return form;
    }

    private async Task<SourceFormModel> SourceFormAsync(SourceFormModel form, CancellationToken ct)
    {
        form.SourceTypes = (await reference.GetAsync(ct)).Choices("exposureSourceType", form.SourceType);
        return form;
    }

    private static PersonInput ToInput(PersonFormModel f) => new(f.PersonId, f.LastName, f.FirstName, f.MiddleName, f.Dob, f.DobVerified,
        f.Sex, f.Ethnicity, f.PreferredLanguage, f.DeceasedDate, f.RaceCodes, Options.FromHex(f.RowVersion));

    private void AddErrors(ValidationException ex) => ModelState.AddErrors(ex);
}
