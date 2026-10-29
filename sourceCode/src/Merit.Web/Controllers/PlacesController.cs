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
/// Places on the person page, including the "Set as primary" dialog (htmx): the row split and county-change preview are
/// computed by the server rule, so the dialog shows exactly what saving will do.
/// </summary>
[Route("people/{personId:guid}/places")]
public sealed class PlacesController(IClock clock, PlaceService places, PersonService people, PersonPageBuilder builder,
                                     IReferenceDataProvider reference, ConfigService config) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Index(Guid personId, CancellationToken ct)
    {
        if (!Request.IsPartial())
        {
            return RedirectToAction("Details", "People", new { id = personId });
        }

        return PartialView("_Places", await builder.PlacesAsync(personId, await places.GetPlacesAsync(personId, ct), true, ct));
    }

    [HttpGet("add")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Add(Guid personId, string? role, CancellationToken ct)
        => PartialView("_PlaceForm", await FormAsync(new PlaceFormModel
        {
            PersonId = personId,
            Role = role ?? "residence",
            StartDate = clock.Today,
        }, ct));

    /// <summary>Adds a place. An overlapping residence re-renders the form with the primary choice; it cannot save without one.</summary>
    [HttpPost("add")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Add(Guid personId, PlaceFormModel form, CancellationToken ct)
    {
        form.PersonId = personId;
        if (form.StartDate is null)
        {
            ModelState.AddModelError(nameof(form.StartDate), "Enter the start date.");
            return this.Invalid("_PlaceForm", await FormAsync(form, ct));
        }

        var input = ToInput(form);
        bool isAdult = await IsAdultAsync(personId, ct);
        try
        {
            var (plan, _) = await places.PlanAddPlaceAsync(input, isAdult, ct);
            if (!plan.IsValid)
            {
                form.NeedsPrimaryChoice = form.Role == "residence";
                form.OverlapMessage = string.Join(" ", plan.Errors);
                return this.Invalid("_PlaceForm", await FormAsync(form, ct));
            }

            await places.AddPlaceAsync(input, isAdult, ct);
            Response.Announce(plan.Warnings.Count > 0 ? "Place added. " + string.Join(" ", plan.Warnings) : "Place added.");
            if (plan.CountyChanges)
            {
                Response.Refresh("merit:personHeader");
            }
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_PlaceForm", await FormAsync(form, ct), ex);
        }

        return PartialView("_Places", await builder.PlacesAsync(personId, await places.GetPlacesAsync(personId, ct), true, ct));
    }

    [HttpGet("{personLocationId:guid}/primary")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> SetPrimary(Guid personId, Guid personLocationId, CancellationToken ct)
    {
        var row = (await places.GetPlacesAsync(personId, ct)).FirstOrDefault(p => p.PersonLocationId == personLocationId)
                  ?? throw new NotFoundException();
        return PartialView("_SetPrimary", await DialogAsync(new SetPrimaryModel
        {
            PersonId = personId,
            PersonLocationId = personLocationId,
            AddressLabel = row.AddressLabel,
            StartDate = clock.Today,
        }, ct));
    }

    /// <summary>Shows the planned row split and county change without saving.</summary>
    [HttpPost("{personLocationId:guid}/primary/preview")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> PreviewPrimary(Guid personId, Guid personLocationId, SetPrimaryModel form, CancellationToken ct)
    {
        form.PersonId = personId;
        form.PersonLocationId = personLocationId;
        if (form.StartDate is null)
        {
            ModelState.AddModelError(nameof(form.StartDate), "Enter the date the new primary residence starts.");
            return this.Invalid("_SetPrimary", await DialogAsync(form, ct));
        }

        var plan = await places.PreviewSetPrimaryAsync(personId, personLocationId, form.StartDate.Value, form.PrimaryReason,
            await IsAdultAsync(personId, ct), ct);
        form.Changes = plan.Changes.Select(c => c.Description).ToList();
        form.Errors = plan.Errors;
        form.Warnings = plan.Warnings;
        form.CountyBefore = plan.CountyBefore;
        form.CountyAfter = plan.CountyAfter;
        form.Previewed = plan.IsValid;
        return PartialView("_SetPrimary", await DialogAsync(form, ct));
    }

    [HttpPost("{personLocationId:guid}/primary")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> ConfirmPrimary(Guid personId, Guid personLocationId, SetPrimaryModel form, CancellationToken ct)
    {
        if (form.StartDate is null || string.IsNullOrWhiteSpace(form.PrimaryReason))
        {
            form.PersonId = personId;
            form.PersonLocationId = personLocationId;
            ModelState.AddModelError(nameof(form.PrimaryReason), "Choose a reason and a start date.");
            return this.Invalid("_SetPrimary", await DialogAsync(form, ct));
        }

        var plan = await places.SetPrimaryAsync(personId, personLocationId, form.StartDate.Value, form.PrimaryReason,
            await IsAdultAsync(personId, ct), ct);
        Response.Announce(plan.CountyChanges
            ? $"Primary residence changed. County: {plan.CountyBefore ?? "none"} to {plan.CountyAfter ?? "none"}."
            : "Primary residence changed.");
        Response.Refresh("merit:personHeader", "merit:closeDialog");
        return PartialView("_Places", await builder.PlacesAsync(personId, await places.GetPlacesAsync(personId, ct), true, ct));
    }

    [HttpPost("{personLocationId:guid}/end")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> End(Guid personId, Guid personLocationId, DateOnly? endDate, CancellationToken ct)
    {
        await places.EndPlaceAsync(personId, personLocationId, endDate ?? clock.Today, ct);
        Response.Announce("Place ended.");
        return PartialView("_Places", await builder.PlacesAsync(personId, await places.GetPlacesAsync(personId, ct), true, ct));
    }

    [HttpPost("{personLocationId:guid}/verify")]
    [Authorize(Policy = Permissions.PersonEdit)]
    public async Task<IActionResult> Verify(Guid personId, Guid personLocationId, CancellationToken ct)
    {
        await places.VerifyPlaceAsync(personId, personLocationId, ct);
        Response.Announce("Place verified.");
        return PartialView("_Places", await builder.PlacesAsync(personId, await places.GetPlacesAsync(personId, ct), true, ct));
    }

    private async Task<bool> IsAdultAsync(Guid personId, CancellationToken ct)
        => (await people.GetPageAsync(personId, ct)).CurrentPopulation == "adult";

    private static AddPlaceInput ToInput(PlaceFormModel f)
    {
        bool siteRole = f.Role is "childCare" or "school";
        if (siteRole)
        {
            // The site picker posts "siteId:locationId", so a child-care or school row resolves strictly from the
            // organization's site and never from hidden residence fields.
            f.OrganizationSiteId = null;
            f.LocationId = null;
            f.LocationLabel = null;
            if (!string.IsNullOrEmpty(f.OrganizationSite) && f.OrganizationSite.Split(':') is [var site, var loc]
                && Guid.TryParse(site, out var siteId) && Guid.TryParse(loc, out var locId))
            {
                f.OrganizationSiteId = siteId;
                f.LocationId = locId;
            }
        }
        else
        {
            // Non-site roles resolve strictly from the existing-location picker or typed address, ignoring any hidden site field.
            f.OrganizationSite = null;
            f.OrganizationSiteId = null;
            f.OrganizationLabel = null;
        }

        AddressInput? address = !siteRole && f.LocationId is null
            ? new AddressInput(f.AddressLine1, f.Unit, f.City, f.State, f.Zip5, "US", f.CountyFips, f.IsCityOnly, f.YearBuilt)
            : null;
        bool? newIsPrimary = f.PrimaryChoice switch { "new" => true, "existing" => false, _ => null };
        return new AddPlaceInput(f.PersonId, f.Role, f.LocationId, f.OrganizationSiteId, address, f.ScheduleType, f.HoursPerWeek,
            f.ScheduleNote, f.StartDate!.Value, f.EndDate, f.Source, newIsPrimary, f.PrimaryReason);
    }

    private async Task<PlaceFormModel> FormAsync(PlaceFormModel form, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        form.Roles = refData.Choices("locationRole", form.Role);
        form.Schedules = refData.Choices("scheduleType", form.ScheduleType, includeBlank: true);
        form.Reasons = refData.Choices("primaryReason", form.PrimaryReason, includeBlank: true);
        var counties = await config.SubJurisdictionsAsync(ct);
        form.Counties = new[] { new SelectOption(string.Empty, "- Not known yet -", form.CountyFips is null) }
            .Concat(counties.Select(c => new SelectOption(c.CountyFips, c.SubJurisdictionName, c.CountyFips == form.CountyFips))).ToList();
        return form;
    }

    private async Task<SetPrimaryModel> DialogAsync(SetPrimaryModel form, CancellationToken ct)
    {
        form.Reasons = (await reference.GetAsync(ct)).Choices("primaryReason", form.PrimaryReason, includeBlank: true);
        return form;
    }
}
