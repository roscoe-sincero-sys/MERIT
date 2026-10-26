using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Merit.Web.Controllers;

/// <summary>
/// The reporter registry (htmx): laboratories, facilities and providers with their CLIA numbers and NPIs, one-at-a-time
/// entry, and a CSV import that is always checked first. The checked file waits in memory for 30 minutes under a
/// token tied to the user, so confirming the import does not need a second upload.
/// </summary>
[Route("reporters")]
public sealed class ReportersController(RegistryService registry, IMemoryCache cache, ICurrentSession session) : Controller
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(30);

    [HttpGet("")]
    [Authorize(Policy = Permissions.PersonView)]
    public async Task<IActionResult> Index(string? q, string? kind, string? message, CancellationToken ct)
        => this.PageOrPartial("Index", "_List", await ListAsync(q, kind, message, new ReporterFormModel(), ct));

    [HttpPost("")]
    [Authorize(Policy = Permissions.RegistryEdit)]
    public async Task<IActionResult> Save(ReporterFormModel form, CancellationToken ct)
    {
        try
        {
            var counts = await registry.SaveAsync(new ReporterInput(form.Kind, form.Name, form.LastName, form.FirstName, form.Credential, form.Npi,
                form.Clia, form.Phone), ct);
            string message = counts.AddedCount > 0 ? "Added to the registry." : "Registry entry updated.";
            return View("Index", await ListAsync(form.Clia ?? form.Npi, null, message, new ReporterFormModel(), ct));
        }
        catch (ValidationException ex)
        {
            foreach (var (k, v) in ex.Errors)
            {
                ModelState.AddModelError(k, string.Join(" ", v));
            }

            return View("Index", await ListAsync(null, null, null, form, ct));
        }
    }

    [HttpGet("import")]
    [Authorize(Policy = Permissions.RegistryEdit)]
    public IActionResult Import() => View(new RegistryImportModel());

    [HttpGet("template.csv")]
    [Authorize(Policy = Permissions.RegistryEdit)]
    public IActionResult Template()
        => File(System.Text.Encoding.UTF8.GetBytes(RegistryService.Template()), "text/csv", "merit-reporters-template.csv");

    [HttpPost("import/check")]
    [Authorize(Policy = Permissions.RegistryEdit)]
    [RequestSizeLimit(RegistryService.MaxFileBytes + 65536)]
    public async Task<IActionResult> Check(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return View("Import", new RegistryImportModel { Error = "Choose a CSV file to check." });
        }

        if (file.Length > RegistryService.MaxFileBytes)
        {
            return View("Import", new RegistryImportModel { Error = "The file is larger than 2 MB; split it into smaller files." });
        }

        using var reader = new StreamReader(file.OpenReadStream());
        string content = await reader.ReadToEndAsync(ct);
        try
        {
            var check = registry.CheckFile(content);
            string token = Guid.NewGuid().ToString("N");
            cache.Set(PendingKey(token), content, PendingLifetime);
            return View("Import", new RegistryImportModel { FileName = file.FileName, Check = check, Token = token });
        }
        catch (ValidationException ex)
        {
            return View("Import", new RegistryImportModel { FileName = file.FileName, Error = string.Join(" ", ex.Errors.Values.SelectMany(v => v)) });
        }
    }

    [HttpPost("import")]
    [Authorize(Policy = Permissions.RegistryEdit)]
    public async Task<IActionResult> Confirm(string? token, string? fileName, CancellationToken ct)
    {
        if (token is null || !cache.TryGetValue(PendingKey(token), out string? content) || content is null)
        {
            return View("Import", new RegistryImportModel { Error = "The checked file has expired. Check it again, then import." });
        }

        cache.Remove(PendingKey(token));
        var (check, counts) = await registry.ImportFileAsync(content, ct);
        return View("Import", new RegistryImportModel { FileName = fileName, Check = check, Counts = counts });
    }

    private string PendingKey(string token) => $"registry-import:{session.UserId}:{token}";

    private async Task<ReportersModel> ListAsync(string? q, string? kind, string? message, ReporterFormModel form, CancellationToken ct)
        => new(await registry.ListAsync(q, kind, ct), q, kind, message, session.Has(Permissions.RegistryEdit), form);
}
