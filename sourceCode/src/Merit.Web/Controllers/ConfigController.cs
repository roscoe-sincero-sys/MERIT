using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Extracts;
using Merit.Application.Intake;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>Threshold, settings and analyte configuration screens (htmx): small tables and forms.</summary>
[Route("config")]
public sealed class ConfigController(ConfigService config, IReferenceDataProvider reference, ICurrentSession session) : Controller
{
    [HttpGet("thresholds")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Thresholds(CancellationToken ct)
        => this.PageOrPartial("Thresholds", "_ThresholdTable",
            new ThresholdListModel(await config.ThresholdsAsync(ct), session.Has(Permissions.ConfigEdit)));

    [HttpGet("thresholds/new")]
    [HttpGet("thresholds/{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> EditThreshold(Guid? id, CancellationToken ct)
    {
        var form = new ThresholdFormModel { EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) };
        if (id is Guid tid)
        {
            var t = (await config.ThresholdsAsync(ct)).FirstOrDefault(x => x.ThresholdId == tid) ?? throw new NotFoundException();
            form = new ThresholdFormModel
            {
                ThresholdId = t.ThresholdId, AnalyteSpecimenId = t.AnalyteSpecimenId, Population = t.Population,
                AgeMinMonths = t.AgeMinMonths, AgeMaxMonths = t.AgeMaxMonths, Speciation = t.Speciation, SampleType = t.SampleType,
                ThresholdType = t.ThresholdType, ActionCodeId = t.ActionCodeId, DueIntervalDays = t.DueIntervalDays,
                LowerValue = t.LowerValue, UpperValue = t.UpperValue, Unit = t.Unit, Comparator = t.Comparator,
                EffectiveFrom = t.EffectiveFrom, EffectiveTo = t.EffectiveTo, Authority = t.Authority, IsActive = t.IsActive,
                RowVersion = Options.Hex(t.RowVersion),
            };
        }

        return this.PageOrPartial("EditThreshold", "_ThresholdForm", await ThresholdFormAsync(form, ct));
    }

    [HttpPost("thresholds")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> SaveThreshold(ThresholdFormModel form, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        var specimen = form.AnalyteSpecimenId is Guid sid ? refData.Specimen(sid) : null;
        if (specimen is null || form.LowerValue is null || form.EffectiveFrom is null)
        {
            ModelState.AddModelError(nameof(form.LowerValue), "Choose the analyte and specimen, and enter the level and start date.");
            return this.Invalid("_ThresholdForm", await ThresholdFormAsync(form, ct));
        }

        try
        {
            await config.SaveThresholdAsync(new ThresholdSave(form.ThresholdId ?? Guid.NewGuid(), specimen.AnalyteSpecimenId, form.Population,
                form.AgeMinMonths, form.AgeMaxMonths, NullIfEmpty(form.Speciation), NullIfEmpty(form.SampleType), form.ThresholdType,
                form.ActionCodeId, form.DueIntervalDays, form.LowerValue.Value, form.UpperValue, specimen.CanonicalUnit, form.Comparator,
                form.EffectiveFrom.Value, form.EffectiveTo, form.Authority, form.IsActive, Options.FromHex(form.RowVersion)), ct);
        }
        catch (ValidationException ex)
        {
            return this.Invalid("_ThresholdForm", await ThresholdFormAsync(form, ct), ex);
        }

        if (Request.IsHtmx())
        {
            Response.Headers["HX-Redirect"] = Url.Action(nameof(Thresholds));
            return Ok();
        }

        return RedirectToAction(nameof(Thresholds));
    }

    [HttpGet("settings")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var all = await config.SettingsAsync(true, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var current = all.Where(s => s.EffectiveFrom <= today && (s.EffectiveTo is null || s.EffectiveTo >= today))
            .GroupBy(s => s.SettingKey).Select(g => g.OrderByDescending(s => s.EffectiveFrom).First()).OrderBy(s => s.SettingKey).ToList();
        return this.PageOrPartial("Settings", "_SettingsTable", new SettingsModel(current, all, session.Has(Permissions.ConfigEdit)));
    }

    [HttpPost("settings/{key}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> SaveSetting(string key, string? settingValue, DateOnly? effectiveFrom, CancellationToken ct)
    {
        await config.SaveSettingAsync(key, settingValue, effectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
        Response.Announce($"Setting {key} changed.");
        return await Settings(ct);
    }

    [HttpGet("analytes")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> Analytes(CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        return this.PageOrPartial("Analytes", "_AnalyteTable",
            new AnalytesModel(refData.Set.Analytes, refData.Set.AnalyteSpecimens, session.Has(Permissions.ConfigEdit)));
    }

    [HttpPost("analytes/{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> SaveAnalyte(Guid id, bool isActive, int sortOrder, CancellationToken ct)
    {
        await config.SaveAnalyteAsync(id, isActive, sortOrder, ct);
        Response.Announce("Analyte saved.");
        return await Analytes(ct);
    }

    private async Task<ThresholdFormModel> ThresholdFormAsync(ThresholdFormModel form, CancellationToken ct)
    {
        var refData = await reference.GetAsync(ct);
        form.Specimens = refData.Set.AnalyteSpecimens
            .Select(s => new SelectOption(s.AnalyteSpecimenId.ToString(),
                $"{refData.Analyte(s.AnalyteId)?.DisplayName} - {Options.Humanize(s.Specimen)} ({s.CanonicalUnit})",
                s.AnalyteSpecimenId == form.AnalyteSpecimenId)).ToList();
        form.ActionCodes = new[] { new SelectOption(string.Empty, "- None -", form.ActionCodeId is null) }
            .Concat(refData.Set.ActionCodes.Select(a => new SelectOption(a.ActionCodeId.ToString(), a.DisplayName, a.ActionCodeId == form.ActionCodeId)))
            .ToList();
        return form;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>User administration (htmx forms): accounts, profile assignments at tenant nodes, MFA resets.</summary>
[Route("admin")]
public sealed class AdminController(AdminService admin, AuditService audit, ICurrentSession session) : Controller
{
    [HttpGet("users")]
    [Authorize(Policy = Permissions.TenantAdminister)]
    public async Task<IActionResult> Users(string? find, CancellationToken ct)
    {
        // Exact user name: give someone who works elsewhere (for example two counties) access where you administer.
        if (!string.IsNullOrWhiteSpace(find) && await admin.FindUserAsync(find, ct) is { } found)
        {
            return RedirectToAction(nameof(EditUser), new { id = found.AppUserId });
        }

        ViewData["findMessage"] = string.IsNullOrWhiteSpace(find) ? null : $"No active account is called \"{find}\".";
        return View(new UsersModel(await admin.UsersAsync(ct)));
    }

    [HttpGet("users/new")]
    [HttpGet("users/{id:guid}")]
    [Authorize(Policy = Permissions.TenantAdminister)]
    public async Task<IActionResult> EditUser(Guid? id, CancellationToken ct)
    {
        var form = new UserFormModel();
        if (id is Guid uid)
        {
            var u = await admin.UserAsync(uid, ct);
            form = new UserFormModel
            {
                AppUserId = uid, UserName = u.User.UserName, DisplayName = u.User.DisplayName, Email = u.User.Email,
                IsActive = u.User.IsActive, MfaEnabled = u.User.MfaEnabled, Assignments = u.Assignments, HomeTenantId = u.User.HomeTenantId,
                HomeTenantName = u.User.HomeTenantName, CanManageAccount = u.User.CanManageAccount,
                CanViewAs = !session.IsPlatformAdmin && u.User.HomeTenantType != "platform" && u.User.AppUserId != session.UserId,
            };
        }

        return View(await UserFormAsync(form, ct));
    }

    [HttpPost("users")]
    [Authorize(Policy = Permissions.TenantAdminister)]
    public async Task<IActionResult> SaveUser(UserFormModel form, CancellationToken ct)
    {
        try
        {
            var id = await admin.SaveUserAsync(new UserInput(form.AppUserId, form.UserName, form.DisplayName, form.Email, form.IsActive,
                form.NewPassword, form.HomeTenantId), ct);
            return RedirectToAction(nameof(EditUser), new { id });
        }
        catch (ValidationException ex)
        {
            foreach (var (k, v) in ex.Errors)
            {
                ModelState.AddModelError(k, string.Join(" ", v));
            }

            return View("EditUser", await UserFormAsync(form, ct));
        }
    }

    [HttpPost("users/{id:guid}/assignments")]
    [Authorize(Policy = Permissions.TenantAdminister)]
    public async Task<IActionResult> Assign(Guid id, Guid roleId, Guid tenantId, bool includeDescendants, string? accessLevel, CancellationToken ct)
    {
        try
        {
            await admin.AssignAsync(id, roleId, tenantId, includeDescendants, accessLevel ?? "full", DateOnly.FromDateTime(DateTime.UtcNow), null, ct);
        }
        catch (MeritException ex) when (ex is ValidationException or PermissionDeniedException)
        {
            TempData["userError"] = ex.Message;
        }

        return RedirectToAction(nameof(EditUser), new { id });
    }

    [HttpPost("users/{id:guid}/assignments/{assignmentId:guid}/end")]
    [Authorize(Policy = Permissions.TenantAdminister)]
    public async Task<IActionResult> EndAssignment(Guid id, Guid assignmentId, CancellationToken ct)
    {
        var u = await admin.UserAsync(id, ct);
        var a = u.Assignments.FirstOrDefault(x => x.UserAssignmentId == assignmentId) ?? throw new NotFoundException();
        await admin.EndAssignmentAsync(id, a, ct);
        return RedirectToAction(nameof(EditUser), new { id });
    }

    [HttpPost("users/{id:guid}/reset-mfa")]
    [Authorize(Policy = Permissions.TenantAdminister)]
    public async Task<IActionResult> ResetMfa(Guid id, CancellationToken ct)
    {
        await admin.ResetMfaAsync(id, ct);
        return RedirectToAction(nameof(EditUser), new { id });
    }

    [HttpGet("audit")]
    [Authorize(Policy = Permissions.AuditView)]
    public async Task<IActionResult> Audit(Guid? entityId, CancellationToken ct)
        => View(new AuditModel(await audit.SearchAsync(session, null, entityId, null, ct), entityId));

    private async Task<UserFormModel> UserFormAsync(UserFormModel form, CancellationToken ct)
    {
        form.Roles = (await admin.RolesAsync(ct))
            .Select(r => new SelectOption(r.RoleId.ToString(), $"{r.RoleName} ({r.RootTenantName})")).ToList();
        form.Tenants = (await admin.TenantsAsync(ct))
            .Select(t => new SelectOption(t.TenantId.ToString(), new string(' ', t.Depth * 3) + t.TenantName)).ToList();
        return form;
    }
}

/// <summary>Intake upload; batch status cards poll a partial while the worker processes the file.</summary>
[Route("intake")]
public sealed class IntakeController(IntakeService intake) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.IntakeUpload)]
    public async Task<IActionResult> Index(string? message, Guid? duplicateOf, CancellationToken ct)
        => this.PageOrPartial("Index", "_Batches", new IntakeModel(await intake.ProfilesAsync(ct), await intake.BatchesAsync(ct), message, duplicateOf));

    [HttpPost("upload")]
    [Authorize(Policy = Permissions.IntakeUpload)]
    [RequestSizeLimit(IntakeService.MaxFileBytes + 65536)]
    public async Task<IActionResult> Upload(Guid intakeProfileId, IFormFile? file, bool uploadAgain, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return RedirectToAction(nameof(Index), new { message = "Choose a file to upload." });
        }

        // The limit is in bytes: check the upload's size before reading it (issue #20).
        if (file.Length > IntakeService.MaxFileBytes)
        {
            return RedirectToAction(nameof(Index), new { message = "The file is larger than 20 MB; split it or send it through the secure file drop." });
        }

        using var reader = new StreamReader(file.OpenReadStream());
        string content = await reader.ReadToEndAsync(ct);
        try
        {
            await intake.UploadAsync(intakeProfileId, file.FileName, content, uploadAgain, ct);
        }
        catch (ValidationException ex)
        {
            return RedirectToAction(nameof(Index), new { message = string.Join(" ", ex.Errors.Values.SelectMany(v => v)) });
        }
        catch (FileAlreadyReceivedException ex)
        {
            return RedirectToAction(nameof(Index), new
            {
                message = $"This file was already received on {Options.Date(ex.ReceivedUtc)} as {ex.FileName}. It was not processed again.",
                duplicateOf = ex.IntakeBatchId,
            });
        }

        return RedirectToAction(nameof(Index), new { message = "File received. It is being processed; this page updates as it runs." });
    }
}

/// <summary>Federal extracts: status cards poll a partial while a job runs; files download directly.</summary>
[Route("extracts")]
public sealed class ExtractsController(ExtractService extracts) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.ExtractRun)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var runs = await extracts.RunsAsync(ct);
        // The form starts on the quarter that last ended: the one due to CDC.
        var lastQuarterEnd = new DateTime(DateTime.UtcNow.Year, ((DateTime.UtcNow.Month - 1) / 3 * 3) + 1, 1).AddDays(-1);
        var model = new ExtractsModel(runs, runs.Any(r => r.RunStatus is "queued" or "running"), lastQuarterEnd.Year, ((lastQuarterEnd.Month - 1) / 3) + 1);
        return this.PageOrPartial("Index", "_Runs", model);
    }

    [HttpPost("")]
    [Authorize(Policy = Permissions.ExtractRun)]
    public async Task<IActionResult> Create(string extractType, int year, int? quarter, CancellationToken ct)
    {
        await extracts.RequestAsync(extractType, year, quarter, ct);
        Response.Announce($"{extractType} extract requested.");
        return await Index(ct);
    }

    [HttpGet("{id:guid}/download")]
    [Authorize(Policy = Permissions.ExtractRun)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var run = await extracts.DownloadAsync(id, ct);
        return File(System.Text.Encoding.ASCII.GetBytes(run.FileContent!), run.ExtractType == "ABLES" ? "text/csv" : "text/plain",
            run.FileName ?? "extract.txt");
    }

    [HttpGet("{id:guid}/findings")]
    [Authorize(Policy = Permissions.ExtractRun)]
    public async Task<IActionResult> Findings(Guid id, CancellationToken ct)
    {
        var (run, findings) = await extracts.FindingsAsync(id, ct);
        return PartialView("_Findings", new ExtractFindingsModel(run, findings));
    }

    [HttpPost("{id:guid}/submitted")]
    [Authorize(Policy = Permissions.ExtractSubmit)]
    public async Task<IActionResult> MarkSubmitted(Guid id, CancellationToken ct)
    {
        await extracts.MarkSubmittedAsync(id, ct);
        Response.Announce("Extract marked submitted.");
        return await Index(ct);
    }
}
