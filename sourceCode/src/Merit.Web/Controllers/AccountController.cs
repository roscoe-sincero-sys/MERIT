using System.Globalization;
using System.Security.Claims;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Merit.Web.Controllers;

/// <summary>
/// Sign-in and account pages: plain Razor forms without htmx. Password first, then a required second factor; a user
/// with no authenticator enrolls at first sign-in. Only after both factors is the session scope created.
/// </summary>
[Route("account")]
public sealed class AccountController(AccountService accounts, QrCodes qr, SessionRenewal sessions) : Controller
{
    [HttpGet("signin")]
    [AllowAnonymous]
    public IActionResult SignIn(string? returnUrl) => View(new SignInModel { ReturnUrl = returnUrl });

    [HttpPost("signin")]
    [AllowAnonymous]
    [EnableRateLimiting(SignInRateLimits.Password)]
    public async Task<IActionResult> SignIn(SignInModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var step = await accounts.CheckPasswordAsync(model.UserName!, model.Password!, ClientAddress(), ct);
        switch (step.Status)
        {
            case SignInStatus.LockedOut:
                model.Message = "Too many failed attempts. The account is locked for 15 minutes; an administrator can unlock it.";
                return View(model);
            case SignInStatus.Failed:
                model.Message = "The user name or password is not right.";
                return View(model);
        }

        var claims = new List<Claim>
        {
            new(MeritClaims.UserId, step.AppUserId!.Value.ToString()),
            new(MeritClaims.UserName, step.UserName!),
            new(MeritClaims.SecurityStamp, step.SecurityStamp!.Value.ToString()),
            new(MeritClaims.MfaStep, step.Status == SignInStatus.NeedsMfaEnrollment ? "enroll" : "verify"),
            new(MeritClaims.IsAdministrator, step.IsAdministrator ? "1" : "0"),
        };
        if (step.Status == SignInStatus.NeedsMfaEnrollment)
        {
            claims.Add(new Claim(MeritClaims.EnrollSecret, AccountService.BeginEnrollment(step.UserName!).SecretBase32));
        }

        await HttpContext.SignInAsync(AuthSchemes.PendingMfa,
            new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.PendingMfa)),
            new AuthenticationProperties { RedirectUri = SafeReturn(model.ReturnUrl) });
        TempData["returnUrl"] = SafeReturn(model.ReturnUrl);
        return RedirectToAction(step.Status == SignInStatus.NeedsMfaEnrollment ? nameof(Enroll) : nameof(Mfa));
    }

    [HttpGet("enroll")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    public IActionResult Enroll()
    {
        var secret = User.FindFirstValue(MeritClaims.EnrollSecret);
        if (secret is null)
        {
            return RedirectToAction(nameof(SignIn));
        }

        return View(BuildEnroll(secret));
    }

    [HttpPost("enroll")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    [EnableRateLimiting(SignInRateLimits.Code)]
    public async Task<IActionResult> Enroll(EnrollModel model, CancellationToken ct)
    {
        var secret = User.FindFirstValue(MeritClaims.EnrollSecret);
        var userId = Guid.Parse(User.FindFirstValue(MeritClaims.UserId)!, CultureInfo.InvariantCulture);
        if (secret is null)
        {
            return RedirectToAction(nameof(SignIn));
        }

        try
        {
            var codes = await accounts.CompleteEnrollmentAsync(userId, secret, model.Code ?? string.Empty, ct);
            await CompleteSignInAsync(userId, ct);
            return View("RecoveryCodes", new RecoveryCodesModel(codes));
        }
        catch (ValidationException ex)
        {
            var again = BuildEnroll(secret);
            again.Message = ex.Errors.Values.SelectMany(v => v).FirstOrDefault();
            return View(again);
        }
    }

    [HttpGet("mfa")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    public IActionResult Mfa() => View(new MfaModel());

    [HttpPost("mfa")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    [EnableRateLimiting(SignInRateLimits.Code)]
    public async Task<IActionResult> Mfa(MfaModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var userName = User.FindFirstValue(MeritClaims.UserName)!;
        var userId = Guid.Parse(User.FindFirstValue(MeritClaims.UserId)!, CultureInfo.InvariantCulture);
        if (!await accounts.VerifyMfaAsync(userName, model.Code!, ClientAddress(), ct))
        {
            model.Message = "That code did not work. Enter the current code from your authenticator app, or one of your recovery codes.";
            model.Code = null;
            return View(model);
        }

        await CompleteSignInAsync(userId, ct);
        return LocalRedirect(TempData["returnUrl"] as string ?? "/");
    }

    [HttpPost("signout")]
    [Authorize(Policy = Permissions.SignedIn)]
    public new async Task<IActionResult> SignOut()
    {
        if (Guid.TryParse(User.FindFirstValue(MeritClaims.ScopeKey), CultureInfo.InvariantCulture, out var scope))
        {
            await accounts.SignOutAsync(scope);
        }

        await HttpContext.SignOutAsync(AuthSchemes.Session);
        Response.Cookies.Delete("XSRF-TOKEN");
        return RedirectToAction(nameof(SignIn));
    }

    [HttpGet("denied")]
    [AllowAnonymous]
    public IActionResult Denied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Error", new AlertModel("warning", "Not permitted", "Your profile does not allow this page."));
    }

    private async Task CompleteSignInAsync(Guid userId, CancellationToken ct)
    {
        // Every sign-in starts on the master profile; the person switches from the header when they want another.
        await HttpContext.SignOutAsync(AuthSchemes.PendingMfa);
        await sessions.SignInAsync(HttpContext, userId, ct);
    }

    private EnrollModel BuildEnroll(string secret)
    {
        var userName = User.FindFirstValue(MeritClaims.UserName)!;
        var uri = Totp.ProvisioningUri(AccountService.Issuer, userName, Totp.FromBase32(secret));
        return new EnrollModel
        {
            SecretBase32 = string.Join(' ', Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - (i * 4))))),
            ProvisioningUri = uri,
            QrSvg = qr.Svg(uri),
            IsAdministrator = User.FindFirstValue(MeritClaims.IsAdministrator) == "1",
        };
    }

    private string? ClientAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string SafeReturn(string? url) => !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) ? url : "/";
}
