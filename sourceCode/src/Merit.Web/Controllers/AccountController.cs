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
using Microsoft.Extensions.Options;

namespace Merit.Web.Controllers;

/// <summary>
/// Sign-in and account pages: plain Razor forms without htmx. Password first, then a required second factor; a user
/// with no authenticator enrolls at first sign-in. Only after both factors is the session scope created.
/// </summary>
[Route("account")]
public sealed class AccountController(AccountService accounts, EmailCodeService emailCodes, QrCodes qr, SessionRenewal sessions,
                                      RecoveryCodesPending recoveryPending, StepUp stepUp, IOptions<EmailCodeOptions> codeOptions) : Controller
{
    /// <summary>Shown whenever a code could not be sent, with a support reference; the cause stays in the log.</summary>
    public const string DeliveryFailedMessage = "We couldn't send your verification code. Please try again. If the problem continues, contact support.";

    private const string SentKey = "emailCode.sent";
    private const string ResendAtKey = "emailCode.resendAt";
    private const string NoticeKey = "emailCode.notice";
    private const string FailedKey = "emailCode.reference";

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
            var codes = await accounts.CompleteEnrollmentAsync(userId, secret, model.Code ?? string.Empty, ClientAddress(), ct);
            var destination = await PendingReturnAsync();
            await CompleteSignInAsync(userId, ct);
            // Until acknowledged, every other page leads back to these codes (issue #156).
            recoveryPending.Hold(Response, userId, codes, destination);
            Response.Headers.CacheControl = "no-store";
            return View("RecoveryCodes", new RecoveryCodesModel(codes, destination));
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
    public async Task<IActionResult> Mfa(bool useRecoveryCode = false, CancellationToken ct = default)
        => View(new MfaModel
        {
            UseRecoveryCode = useRecoveryCode,
            Email = await PendingEmailOptionAsync(ct),
            Notice = TempData[NoticeKey] as string,
        });

    [HttpPost("mfa")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    [EnableRateLimiting(SignInRateLimits.Code)]
    public async Task<IActionResult> Mfa(MfaModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            model.Email = await PendingEmailOptionAsync(ct);
            return View(model);
        }

        var userName = User.FindFirstValue(MeritClaims.UserName)!;
        var userId = Guid.Parse(User.FindFirstValue(MeritClaims.UserId)!, CultureInfo.InvariantCulture);
        if (!await accounts.VerifyMfaAsync(userName, model.Code!, ClientAddress(), ct))
        {
            model.Email = await PendingEmailOptionAsync(ct);
            model.Message = model.UseRecoveryCode
                ? "That recovery code did not work. Try an unused recovery code, or ask your MERIT administrator for an MFA reset after verifying your identity."
                : "That code did not work. Enter the current code from your authenticator app, or choose Can't access your authenticator? for help.";
            model.Code = null;
            ModelState.Remove(nameof(MfaModel.Code));
            return View(model);
        }

        var destination = await PendingReturnAsync();
        await CompleteSignInAsync(userId, ct);
        return LocalRedirect(destination);
    }

    /// <summary>Emails a sign-in code. Every outcome lands on the code page, or back on the MFA page when email codes are not available.</summary>
    [HttpPost("mfa/email/send")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    [EnableRateLimiting(SignInRateLimits.EmailCode)]
    public async Task<IActionResult> SendEmailCode(CancellationToken ct)
    {
        var result = await emailCodes.SendAsync(PendingUserId(), EmailCodePurpose.SignIn, ClientAddress(), null, ct);
        if (result.Status == EmailCodeSendStatus.NotAvailable)
        {
            TempData[NoticeKey] = NotAvailableNotice;
            return RedirectToAction(nameof(Mfa));
        }

        Remember(result);
        return RedirectToAction(nameof(EmailCode));
    }

    [HttpGet("mfa/email")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    public async Task<IActionResult> EmailCode(CancellationToken ct)
    {
        var option = await PendingEmailOptionAsync(ct);
        if (option is not { Available: true })
        {
            TempData[NoticeKey] = NotAvailableNotice;
            return RedirectToAction(nameof(Mfa));
        }

        Response.Headers.CacheControl = "no-store";
        return View(Recall(new EmailCodeModel { MaskedEmail = option.MaskedEmail }));
    }

    [HttpPost("mfa/email")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    [EnableRateLimiting(SignInRateLimits.Code)]
    public async Task<IActionResult> EmailCode(EmailCodeModel model, CancellationToken ct)
    {
        var userId = PendingUserId();
        var option = await PendingEmailOptionAsync(ct);
        var page = Recall(new EmailCodeModel { MaskedEmail = option?.MaskedEmail }, afterCheck: true);
        if (!ModelState.IsValid)
        {
            Response.Headers.CacheControl = "no-store";
            return View(page);
        }

        var result = await emailCodes.VerifySignInAsync(userId, User.FindFirstValue(MeritClaims.UserName)!, model.Code, ClientAddress(), ct);
        if (result.Outcome == EmailCodeCheck.Verified)
        {
            ForgetSend();
            var destination = await PendingReturnAsync();
            await CompleteSignInAsync(userId, ct);
            return LocalRedirect(destination);
        }

        return CodeRejected(page, result);
    }

    [HttpPost("restart-signin")]
    [Authorize(AuthenticationSchemes = AuthSchemes.PendingMfa)]
    public async Task<IActionResult> RestartSignIn()
    {
        await HttpContext.SignOutAsync(AuthSchemes.PendingMfa);
        TempData.Remove("returnUrl");
        return RedirectToAction(nameof(SignIn));
    }

    /// <summary>The recovery codes again, when the person left the step without acknowledging them (issue #156).</summary>
    [HttpGet("recovery")]
    [Authorize(Policy = Permissions.SignedIn)]
    public IActionResult RecoveryCodes()
    {
        if (recoveryPending.Waiting(HttpContext) is not { } waiting)
        {
            return LocalRedirect("/");
        }

        Response.Headers.CacheControl = "no-store";
        return View("RecoveryCodes", new RecoveryCodesModel(waiting.Codes, waiting.ReturnUrl));
    }

    [HttpPost("recovery/continue")]
    [Authorize(Policy = Permissions.SignedIn)]
    public IActionResult ContinueAfterRecovery(bool savedCodes, string? returnUrl)
    {
        if (!savedCodes)
        {
            return BadRequest("Save your recovery codes and acknowledge that you have saved them before continuing.");
        }

        RecoveryCodesPending.Release(Response);
        return LocalRedirect(SafeReturn(returnUrl));
    }

    // ------------------------------------------------------------------------------------------ sign-in methods

    /// <summary>The person's sign-in methods: the authenticator app, always on, and email codes where the jurisdiction allows them.</summary>
    [HttpGet("security")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> SignInMethods(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        string? reference = TempData[FailedKey] as string;
        return View(new SignInMethodsModel
        {
            Email = await emailCodes.GetOptionAsync(SignedInUserId(), ct),
            Notice = TempData[NoticeKey] as string,
            SupportReference = reference,
            Message = reference is null ? null : DeliveryFailedMessage,
        });
    }

    /// <summary>Turning email codes on starts with a current authenticator code, then a code sent to the address.</summary>
    [HttpPost("security/email/start")]
    [Authorize(Policy = Permissions.SignedIn)]
    [EnableRateLimiting(SignInRateLimits.EmailCode)]
    public async Task<IActionResult> StartEmailCodes(SignInMethodsModel model, CancellationToken ct)
    {
        var userId = SignedInUserId();
        var option = await emailCodes.GetOptionAsync(userId, ct);
        if (!option.Allowed || option.TurnedOn)
        {
            return RedirectToAction(nameof(SignInMethods));
        }

        if (!await accounts.CheckAuthenticatorCodeAsync(User.FindFirstValue(MeritClaims.UserName)!, model.AuthenticatorCode, ClientAddress(), ct))
        {
            ModelState.AddModelError(nameof(SignInMethodsModel.AuthenticatorCode),
                "That code did not match. Enter the current six-digit code from your authenticator app.");
            Response.Headers.CacheControl = "no-store";
            return View(nameof(SignInMethods), new SignInMethodsModel { Email = option });
        }

        stepUp.Grant(Response, userId);
        return await SendEnrollmentCodeAsync(userId, ct);
    }

    [HttpPost("security/email/resend")]
    [Authorize(Policy = Permissions.SignedIn)]
    [EnableRateLimiting(SignInRateLimits.EmailCode)]
    public async Task<IActionResult> ResendEnrollmentCode(CancellationToken ct)
        => stepUp.Holds(HttpContext) ? await SendEnrollmentCodeAsync(SignedInUserId(), ct) : StartAgain();

    [HttpGet("security/email")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> ConfirmEmailCodes(CancellationToken ct)
    {
        if (!stepUp.Holds(HttpContext))
        {
            return StartAgain();
        }

        var option = await emailCodes.GetOptionAsync(SignedInUserId(), ct);
        if (!option.Allowed || option.TurnedOn)
        {
            return RedirectToAction(nameof(SignInMethods));
        }

        Response.Headers.CacheControl = "no-store";
        return View(nameof(EmailCode), Recall(new EmailCodeModel { MaskedEmail = option.MaskedEmail, ForEnrollment = true }));
    }

    [HttpPost("security/email/confirm")]
    [Authorize(Policy = Permissions.SignedIn)]
    [EnableRateLimiting(SignInRateLimits.Code)]
    public async Task<IActionResult> ConfirmEmailCodes(EmailCodeModel model, CancellationToken ct)
    {
        if (!stepUp.Holds(HttpContext))
        {
            return StartAgain();
        }

        var userId = SignedInUserId();
        var option = await emailCodes.GetOptionAsync(userId, ct);
        var page = Recall(new EmailCodeModel { MaskedEmail = option.MaskedEmail, ForEnrollment = true }, afterCheck: true);
        if (!ModelState.IsValid)
        {
            Response.Headers.CacheControl = "no-store";
            return View(nameof(EmailCode), page);
        }

        var result = await emailCodes.VerifyEnrollmentAsync(userId, model.Code, ClientAddress(), ct);
        if (result.Outcome == EmailCodeCheck.Verified)
        {
            ForgetSend();
            StepUp.Release(Response);
            TempData[NoticeKey] = $"Email codes are on. At sign-in you can choose to get a code at {option.MaskedEmail}. Your authenticator app still works.";
            return RedirectToAction(nameof(SignInMethods));
        }

        return CodeRejected(page, result);
    }

    [HttpPost("security/email/off")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> TurnOffEmailCodes(CancellationToken ct)
    {
        await emailCodes.SaveMyMethodsAsync(false, false, ct);
        TempData[NoticeKey] = "Email codes are off. Sign in with your authenticator app.";
        return RedirectToAction(nameof(SignInMethods));
    }

    [HttpPost("security/preference")]
    [Authorize(Policy = Permissions.SignedIn)]
    public async Task<IActionResult> SavePreferredMethod(string? preferredMethod, CancellationToken ct)
    {
        var option = await emailCodes.GetOptionAsync(SignedInUserId(), ct);
        if (option.TurnedOn)
        {
            bool preferEmail = preferredMethod == "email";
            await emailCodes.SaveMyMethodsAsync(true, preferEmail, ct);
            TempData[NoticeKey] = preferEmail
                ? "Sign-in will offer an email code first. You can still use your authenticator app."
                : "Sign-in will ask for your authenticator app first.";
        }

        return RedirectToAction(nameof(SignInMethods));
    }

    private const string NotAvailableNotice = "Email codes are not available for this account right now. Enter a code from your authenticator app.";

    private async Task<IActionResult> SendEnrollmentCodeAsync(Guid userId, CancellationToken ct)
    {
        var result = await emailCodes.SendAsync(userId, EmailCodePurpose.Enroll, ClientAddress(), null, ct);
        if (result.Status == EmailCodeSendStatus.NotAvailable)
        {
            StepUp.Release(Response);
            TempData[NoticeKey] = "Email codes are not available for your account right now.";
            return RedirectToAction(nameof(SignInMethods));
        }

        Remember(result);
        return LocalRedirect("/account/security/email");
    }

    private RedirectToActionResult StartAgain()
    {
        TempData[NoticeKey] = "To turn on email codes, start again with a current code from your authenticator app.";
        return RedirectToAction(nameof(SignInMethods));
    }

    /// <summary>Carries a send's outcome across the redirect to the code page (post, redirect, get).</summary>
    private void Remember(EmailCodeSendResult result)
    {
        // Unix seconds as text: TempData would turn a date-shaped string back into a DateTime.
        string resendAt = DateTimeOffset.UtcNow.AddSeconds(result.RetryAfterSeconds).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        switch (result.Status)
        {
            case EmailCodeSendStatus.Sent:
                TempData[SentKey] = true;
                TempData[ResendAtKey] = resendAt;
                break;
            case EmailCodeSendStatus.Cooldown:
                TempData[NoticeKey] = $"A code was sent a moment ago. Check your email, including junk or spam folders. You can ask for another code in {Seconds(result.RetryAfterSeconds)}.";
                TempData[ResendAtKey] = resendAt;
                break;
            case EmailCodeSendStatus.Limited:
                TempData[NoticeKey] = $"Too many codes have been sent. You can ask for another in {Minutes(result.RetryAfterSeconds)}, or use your authenticator app.";
                TempData[ResendAtKey] = resendAt;
                break;
            case EmailCodeSendStatus.DeliveryFailed:
                TempData[FailedKey] = result.SupportReference;
                TempData.Remove(ResendAtKey);
                break;
        }
    }

    /// <summary>
    /// The code page's state from the last send. After a wrong code (<paramref name="afterCheck"/>) the "we sent a code"
    /// banner and old notices do not come back; only the resend wait carries on.
    /// </summary>
    private EmailCodeModel Recall(EmailCodeModel model, bool afterCheck = false)
    {
        if (afterCheck)
        {
            TempData.Remove(SentKey);
            TempData.Remove(NoticeKey);
            TempData.Remove(FailedKey);
        }
        else
        {
            model.JustSent = TempData[SentKey] is true;
            model.Notice = TempData[NoticeKey] as string;
            model.SupportReference = TempData[FailedKey] as string;
            model.Message = model.DeliveryFailed ? DeliveryFailedMessage : null;
        }

        if (TempData.Peek(ResendAtKey) is string at && long.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out long resendAt))
        {
            model.ResendInSeconds = (int)Math.Clamp(resendAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, 86_400);
        }

        model.LifetimeMinutes = Math.Max(1, codeOptions.Value.LifetimeSeconds / 60);
        return model;
    }

    private void ForgetSend()
    {
        TempData.Remove(SentKey);
        TempData.Remove(ResendAtKey);
        TempData.Remove(NoticeKey);
        TempData.Remove(FailedKey);
    }

    private ViewResult CodeRejected(EmailCodeModel page, EmailCodeVerifyResult result)
    {
        string message = result.Outcome switch
        {
            EmailCodeCheck.Malformed => "Enter the six-digit code from the email.",
            EmailCodeCheck.Wrong => $"That code did not match. You can try {result.RemainingAttempts} more time{(result.RemainingAttempts == 1 ? "" : "s")} before you need a new code.",
            EmailCodeCheck.Locked => "That code was entered wrong too many times and no longer works. Send a new code, or use your authenticator app.",
            EmailCodeCheck.Expired => "That code has expired. Send a new code.",
            EmailCodeCheck.AccountLocked => "Too many failed attempts. The account is locked for 15 minutes; an administrator can unlock it.",
            EmailCodeCheck.Superseded => "That code was replaced by a newer one. Enter the code from the newest email, or send a new code.",
            _ => "That code can no longer be used. Enter the code from the newest email, or send a new code.",
        };
        ModelState.Remove(nameof(EmailCodeModel.Code));
        ModelState.AddModelError(nameof(EmailCodeModel.Code), message);
        Response.Headers.CacheControl = "no-store";
        return View(nameof(EmailCode), page);
    }

    private async Task<EmailCodeOption?> PendingEmailOptionAsync(CancellationToken ct)
        => User.FindFirstValue(MeritClaims.MfaStep) == "verify" && Guid.TryParse(User.FindFirstValue(MeritClaims.UserId), CultureInfo.InvariantCulture, out var id)
            ? await emailCodes.GetOptionAsync(id, ct)
            : null;

    private Guid PendingUserId() => Guid.Parse(User.FindFirstValue(MeritClaims.UserId)!, CultureInfo.InvariantCulture);

    private Guid SignedInUserId() => Guid.Parse(User.FindFirstValue(MeritClaims.UserId)!, CultureInfo.InvariantCulture);

    private static string Seconds(int seconds) => seconds == 1 ? "1 second" : $"{seconds} seconds";

    private static string Minutes(int seconds)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(seconds / 60.0));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private async Task<string> PendingReturnAsync()
    {
        var pending = await HttpContext.AuthenticateAsync(AuthSchemes.PendingMfa);
        var destination = SafeReturn(pending.Properties?.RedirectUri ?? TempData.Peek("returnUrl") as string);
        TempData.Remove("returnUrl");
        return destination;
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
        RecoveryCodesPending.Release(Response);
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
            AccountName = $"{AccountService.Issuer}: {userName}",
            SecretBase32 = string.Join(' ', Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - (i * 4))))),
            ProvisioningUri = uri,
            QrSvg = qr.Svg(uri),
            IsAdministrator = User.FindFirstValue(MeritClaims.IsAdministrator) == "1",
        };
    }

    private string? ClientAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string SafeReturn(string? url) => !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) ? url : "/";
}
