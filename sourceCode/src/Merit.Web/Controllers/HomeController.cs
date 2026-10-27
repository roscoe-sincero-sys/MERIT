using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>Home dashboard (htmx): server-rendered tiles whose counts refresh by polling a partial.</summary>
public sealed class HomeController(QueueService queues, ICurrentSession session) : Controller
{
    [HttpGet("/")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // A platform administrator (and anyone whose profile has no work queues) starts where their work is.
        if (!session.Has(Permissions.TaskView))
        {
            return Redirect(session.Has(Permissions.TenantAdminister) ? "/admin/users" : "/account/profiles");
        }

        var dashboard = await queues.DashboardAsync(ct);
        var tiles = new QueueTilesModel(await queues.CountsAsync(ct));
        return View(new DashboardModel(dashboard, tiles, session.DisplayName ?? string.Empty, session.ActingTenantName));
    }

    [HttpGet("/home/queues")]
    [Authorize(Policy = Permissions.TaskView)]
    public async Task<IActionResult> Queues(CancellationToken ct)
    {
        var tiles = new QueueTilesModel(await queues.CountsAsync(ct));
        return Request.IsPartial() ? PartialView("_QueueTiles", tiles) : RedirectToAction(nameof(Index));
    }

    [HttpGet("/home/error")]
    [AllowAnonymous]
    public IActionResult Error() => View("Error", new AlertModel("error", "Something went wrong",
        $"The request could not be completed. Reference {HttpContext.TraceIdentifier}."));

    /// <summary>
    /// The page for an error status that came back with no body, such as an address that matches no page (issue #43):
    /// re-executed by the status-code pages middleware, so the browser never shows its own blank error page.
    /// </summary>
    // Re-execution keeps the failed request's method, so the page answers any of them; it changes nothing, so it needs
    // no antiforgery token (a refused token is one of the statuses it shows).
    [HttpGet("/home/status/{code:int}")]
    [HttpPost("/home/status/{code:int}")]
    [HttpPut("/home/status/{code:int}")]
    [HttpPatch("/home/status/{code:int}")]
    [HttpDelete("/home/status/{code:int}")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public IActionResult Status(int code)
    {
        Response.StatusCode = code is >= 400 and <= 599 ? code : StatusCodes.Status500InternalServerError;
        var alert = code switch
        {
            404 => new AlertModel("warning", "Page not found",
                "This address is not part of MERIT, or it has moved. Check the address, or start again from Home or the search box."),
            403 => new AlertModel("warning", "Not permitted", "Your profile does not allow this page."),
            405 or 415 => new AlertModel("warning", "That did not work",
                "The page cannot be used that way. Go back and try again from the screen's own buttons."),
            >= 500 => new AlertModel("error", "Something went wrong",
                $"The request could not be completed. Reference {HttpContext.TraceIdentifier}."),
            _ => new AlertModel("warning", "That did not work", "The request could not be completed. Go back and try again."),
        };
        return View("Error", alert);
    }
}

/// <summary>Global search by name, DOB, address, organization, accession or case number.</summary>
[Route("search")]
public sealed class SearchController(SearchService search) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        var model = new SearchModel(q, string.IsNullOrWhiteSpace(q) ? null : await search.SearchAsync(q, ct));
        return this.PageOrPartial("Index", "_Results", model);
    }
}

/// <summary>
/// The Razor host view for Angular screens under /app/: the shared header and navigation, the antiforgery cookie and the
/// CSP nonce, then Angular bootstraps into the main region. Users see one header whichever front end draws the page.
/// </summary>
[Route("app")]
public sealed class AppHostController(AngularAssets assets) : Controller
{
    [HttpGet("")]
    [HttpGet("{**path}")]
    [Authorize(Policy = Permissions.SignedIn)]
    public IActionResult Index() => View(new AngularHostModel(assets.Scripts, assets.Styles, assets.IsBuilt));
}

/// <summary>
/// Server-side type-ahead: a text input whose hx-get returns a USWDS-styled list of matches after the user pauses
/// typing (ARIA combobox pattern in merit.js); the chosen value posts as a hidden id.
/// </summary>
[Route("lookup")]
public sealed class LookupController(LookupService lookup, PlaceService places, SearchService search) : Controller
{
    [HttpGet("people")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> People(string? q, string field, CancellationToken ct)
    {
        var results = string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2 ? null : await search.SearchAsync(q, ct);
        return PartialView("_Options", new LookupOptionsModel(field, (results?.Persons ?? []).Take(10)
            .Select(p => (p.PersonId, $"{p.LastName}, {p.FirstName}", (string?)(p.Dob is null ? "DOB unknown" : "b. " + Options.Date(p.Dob)))).ToList()));
    }

    [HttpGet("organizations")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Organizations(string? q, string? type, string field, CancellationToken ct)
    {
        var rows = await lookup.OrganizationsAsync(q, type, ct);
        return PartialView("_Options", new LookupOptionsModel(field,
            rows.Select(r => (r.OrganizationId, r.OrganizationName, (string?)string.Join(", ",
                new[] { r.SiteAddress, r.SiteCity, r.CliaNumber is null ? null : "CLIA " + r.CliaNumber }.Where(s => s is not null)))).ToList()));
    }

    [HttpGet("sites")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Sites(string? q, string? type, string field, CancellationToken ct)
    {
        var rows = await lookup.OrganizationsAsync(q, type, ct);
        return PartialView("_SiteOptions", new LookupOptionsModel(field,
            rows.Where(r => r.OrganizationSiteId is not null && r.SiteLocationId is not null)
                .Select(r => (r.OrganizationSiteId!.Value, r.OrganizationName, (string?)$"{r.SiteAddress}, {r.SiteCity}")).ToList(),
            string.Join(',', rows.Where(r => r.OrganizationSiteId is not null && r.SiteLocationId is not null)
                .Select(r => r.SiteLocationId!.Value.ToString()))));
    }

    [HttpGet("providers")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Providers(string? q, string field, CancellationToken ct)
    {
        var rows = await lookup.ProvidersAsync(q, ct);
        return PartialView("_Options", new LookupOptionsModel(field,
            rows.Select(r => (r.ProviderId, $"{r.LastName}, {r.FirstName} {r.Credential}".Trim(),
                (string?)$"NPI {r.Npi} {r.PracticeCity} {r.PracticeState}".Trim())).ToList()));
    }

    [HttpGet("locations")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Locations(string? q, string field, CancellationToken ct)
    {
        var rows = await places.FindLocationsAsync(q, ct);
        return PartialView("_Options", new LookupOptionsModel(field,
            rows.Select(r => (r.LocationId, $"{r.AddressLine1}{(string.IsNullOrEmpty(r.Unit) ? string.Empty : " " + r.Unit)}",
                (string?)$"{r.City}, {r.State} {r.Zip5}{(r.CountyName is null ? string.Empty : " - " + r.CountyName)}")).ToList()));
    }
}
