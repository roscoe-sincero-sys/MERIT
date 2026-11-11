using System.Globalization;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Merit.Web.Controllers;

/// <summary>
/// The sign-in event log's reports (#191): suspicious activity for administrators, and the investigation of user names that
/// matched no account for holders of security.investigate. Names stay hashed until a holder reveals them with a current
/// authenticator code; each reveal, lookup and export is in the audit ledger.
/// </summary>
[Route("admin/sign-in-activity")]
public sealed class SignInActivityController(SignInLogService log, ICurrentSession session, IClock clock) : Controller
{
    [HttpGet("")]
    [Authorize(Policy = Permissions.SecurityReports)]
    public async Task<IActionResult> Index(int hours = 24, CancellationToken ct = default)
        => View(new SignInActivityModel(await log.ActivityAsync(hours, ct), Math.Clamp(hours, 1, 2160), session.Has(Permissions.SecurityInvestigate)));

    /// <summary>Every section of the suspicious-activity report as one file (#272), one row per finding with its section.
    /// Unknown user names stay hashed. The export is audited.</summary>
    [HttpGet("activity.csv")]
    [Authorize(Policy = Permissions.SecurityReports)]
    public async Task<IActionResult> ActivityCsv(int hours = 24, CancellationToken ct = default)
    {
        var r = await log.ExportActivityAsync(hours, ct);
        static string U(DateTime d) => d.ToString("u", CultureInfo.InvariantCulture);
        var rows = new List<IReadOnlyList<object?>>();
        rows.AddRange(r.ByAddress.Select(x => (IReadOnlyList<object?>)["Failures by address", x.ClientAddress, null, null, x.Failures,
            $"{x.DistinctAccounts} accounts, {x.DistinctUnknownNames} unknown names", x.Lockouts, x.RateLimited, U(x.FirstUtc), U(x.LastUtc)]));
        rows.AddRange(r.ByAccount.Select(x => (IReadOnlyList<object?>)["Failures by account", null, x.UserName, x.DisplayName, x.Failures,
            $"{x.DistinctAddresses} addresses", x.Lockouts, null, null, U(x.LastUtc)]));
        rows.AddRange(r.Spraying.Select(x => (IReadOnlyList<object?>)["One address, many accounts", x.ClientAddress, null, null, x.Failures,
            $"{x.Targets} targets", null, null, U(x.FirstUtc), U(x.LastUtc)]));
        rows.AddRange(r.Stuffing.Select(x => (IReadOnlyList<object?>)["One account, many addresses", null, x.UserName ?? ("hash " + x.AttemptedNameHash), x.DisplayName,
            x.Failures, $"{x.Addresses} addresses", null, null, null, U(x.LastUtc)]));
        rows.AddRange(r.SuccessAfterFailures.Select(x => (IReadOnlyList<object?>)["Signed in after failures", x.SuccessAddress, x.UserName, x.DisplayName,
            x.Failures, $"{x.Addresses} addresses", null, null, null, U(x.SuccessUtc)]));
        rows.AddRange(r.LockoutsAndLimits.Select(x => (IReadOnlyList<object?>)["Lockouts and rate limits", x.ClientAddress, x.UserName, null, null,
            x.EventType + (x.Reason is null ? "" : ": " + x.Reason), null, null, null, U(x.EventUtc)]));
        return File(TabularExport.Csv(["Section", "Address", "User name", "Name", "Failures", "Detail", "Lockouts", "Rate limited", "First (UTC)", "Last (UTC)"], rows),
            TabularExport.CsvType, $"merit-sign-in-activity-{clock.Today:yyyy-MM-dd}.csv");
    }

    [HttpGet("unknown-names")]
    [Authorize(Policy = Permissions.SecurityInvestigate)]
    public async Task<IActionResult> UnknownNames(int hours = 168, CancellationToken ct = default)
        => View(new UnknownNamesModel { Hours = Math.Clamp(hours, 1, 2160), Names = await log.UnknownNamesAsync(hours, ct) });

    /// <summary>The names themselves, after a current authenticator code; the reveal is audited.</summary>
    [HttpPost("unknown-names/reveal")]
    [Authorize(Policy = Permissions.SecurityInvestigate)]
    [EnableRateLimiting(SignInRateLimits.Code)]
    public async Task<IActionResult> Reveal(UnknownNamesModel model, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        model.Hours = Math.Clamp(model.Hours, 1, 2160);
        try
        {
            model.Names = await log.RevealUnknownNamesAsync(model.Hours, model.AuthenticatorCode, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            model.Revealed = true;
        }
        catch (ValidationException ex)
        {
            foreach (var (field, messages) in ex.Errors)
            {
                foreach (var message in messages)
                {
                    ModelState.AddModelError(field, message);
                }
            }

            model.Names = await log.UnknownNamesAsync(model.Hours, ct);
        }

        model.AuthenticatorCode = null;
        return View(nameof(UnknownNames), model);
    }

    /// <summary>Every attempt at one typed name, by its hash: nothing is decrypted, and the lookup is audited.</summary>
    [HttpPost("unknown-names/lookup")]
    [Authorize(Policy = Permissions.SecurityInvestigate)]
    public async Task<IActionResult> Lookup(UnknownNamesModel model, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        model.Hours = Math.Clamp(model.Hours, 1, 2160);
        try
        {
            model.Attempts = await log.FindAttemptsAsync(model.LookupName, ct);
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError(nameof(UnknownNamesModel.LookupName), ex.Errors.Values.SelectMany(v => v).First());
        }

        model.Names = await log.UnknownNamesAsync(model.Hours, ct);
        return View(nameof(UnknownNames), model);
    }

    /// <summary>The hashed report as CSV. Names are never exported; the export is audited.</summary>
    [HttpGet("unknown-names.csv")]
    [Authorize(Policy = Permissions.SecurityInvestigate)]
    public async Task<IActionResult> Export(int hours = 168, CancellationToken ct = default)
    {
        var rows = await log.ExportUnknownNamesAsync(hours, ct);
        byte[] csv = TabularExport.Csv(["Name hash", "Attempts", "Addresses", "First (UTC)", "Last (UTC)", "Addresses seen"],
            rows.Select(r => (IReadOnlyList<object?>)[r.Hash, r.Attempts, r.Addresses, r.FirstUtc.ToString("u", CultureInfo.InvariantCulture),
                r.LastUtc.ToString("u", CultureInfo.InvariantCulture), r.TopAddresses]));
        return File(csv, "text/csv", $"merit-unknown-user-names-{clock.Today:yyyy-MM-dd}.csv");
    }
}
