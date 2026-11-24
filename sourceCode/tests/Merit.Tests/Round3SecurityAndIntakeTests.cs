using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Merit.Application.Data;
using Merit.Application.Email;
using Merit.Application.Errors;
using Merit.Application.Intake;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Controllers;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Merit.Tests;

/// <summary>
/// Targeted unit regression tests for PR #308 round-3 findings:
/// B4 (AttemptedNameProtection fails closed outside Development), I6 (CSV row limit with/without trailing newline and
/// terminal batch state in IntakeProcessor), and password-reset factor preservation on current-password reuse.
/// </summary>
public sealed class Round3SecurityAndIntakeTests
{
    [Fact]
    public void AttemptedNameProtectionThrowsOutsideDevelopmentWhenHashKeyMissing()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "Disabled",
                ["EmailCodes:VerifierKey"] = "",
                ["SignInLog:NameHashKey"] = "",
            })
            .Build();
        var env = new StubHostEnvironment("Production");
        var dp = DataProtectionProvider.Create("MERIT-Test");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AttemptedNameProtection(dp, cfg, NullLogger<AttemptedNameProtection>.Instance, env));
        Assert.Contains("SignInLog:NameHashKey", ex.Message, StringComparison.Ordinal);
    }

    private sealed class StubHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Merit.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CsvReaderEnforcesMaxRowsWithAndWithoutTrailingNewline(bool trailingNewline)
    {
        string atLimit = string.Join("\n", Enumerable.Range(1, 5).Select(i => $"a{i},b{i}")) + (trailingNewline ? "\n" : "");
        Assert.Equal(5, CsvReader.Read(atLimit, ',', maxRows: 5).Count);

        string overLimit = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"a{i},b{i}")) + (trailingNewline ? "\n" : "");
        var ex = Assert.Throws<ValidationException>(() => CsvReader.Read(overLimit, ',', maxRows: 5));
        Assert.Contains("split the file", ex.Errors["file"][0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IntakeProcessorMarksBatchFailedWithSplitReasonWhenCsvExceedsRowLimit()
    {
        string hugeCsv = "lastName,firstName\n" + string.Join("\n", Enumerable.Range(1, CsvReader.MaxRows + 1).Select(i => $"Doe{i},Jane"));
        Guid batchId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        var statuses = new List<(string Status, string? Message)>();

        var fakeIntake = FakeIntakeData.Create(
            new IntakeBatchRow
            {
                IntakeBatchId = batchId,
                IntakeProfileId = profileId,
                FileName = "oversized.csv",
                RawContent = hugeCsv,
                BatchStatus = "received",
            },
            new IntakeProfileRow
            {
                IntakeProfileId = profileId,
                FileType = "csv",
                HeaderRow = 1,
                ColumnMapJson = "{}",
                IsActive = true,
            },
            (status, msg) => statuses.Add((status, msg)));

        var processor = new IntakeProcessor(
            null!,
            fakeIntake,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<IntakeProcessor>.Instance);

        var summary = await processor.ProcessAsync(batchId, releasedOnly: false);

        Assert.False(summary.Paused);
        Assert.Contains("split the file", summary.PauseReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, statuses.Count);
        Assert.Equal("parsing", statuses[0].Status);
        Assert.Equal("failed", statuses[1].Status);
        Assert.Contains("split the file", statuses[1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PasswordResetPreservesRecoveryCodeWhenNewPasswordMatchesCurrentPassword()
    {
        var hasher = new IdentityPasswordHasher();
        var protector = new PassThroughSecretProtector();
        var codeHasher = new HmacOneTimeCodeHasher(RandomNumberGenerator.GetBytes(32));

        Guid userId = Guid.NewGuid();
        Guid stamp = Guid.NewGuid();
        Guid resetId = Guid.NewGuid();
        Guid recoveryId = Guid.NewGuid();
        string token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; // 43 base64url chars
        string currentPassword = "Current-Password-Long-Enough-99!";
        string newPassword = "Brand-New-Password-Long-Enough-42!";
        string recoveryCode = "ABCD123456";
        string totpSecret = Totp.ToBase32(Totp.NewSecret());

        var user = new SignInUserRow
        {
            AppUserId = userId,
            UserName = "resetuser",
            DisplayName = "Reset User",
            PasswordHash = hasher.Hash(currentPassword),
            SecurityStamp = stamp,
            MfaSecretProtected = protector.Protect(totpSecret),
            MfaEnabled = true,
            IsActive = true,
        };

        var fakeData = FakeResetAccountData.Create(user, recoveryId, hasher.Hash(recoveryCode));
        var accounts = new AccountService(fakeData, hasher, protector, new EmptyBreachedList(), new NullSignInEvents());
        var passwords = new PasswordService(fakeData, accounts, new FakeEmailSender(), codeHasher,
            Microsoft.Extensions.Options.Options.Create(new PasswordResetOptions()), new NullSignInEvents(), NullLogger<PasswordService>.Instance);

        // 1. Wrong recovery code + current password returns WrongCode (never reveals current-password match).
        var wrong = await passwords.CompleteResetAsync(resetId, token, "WRONG12345", currentPassword, currentPassword, "127.0.0.1");
        Assert.Equal(PasswordResetOutcome.WrongCode, wrong.Outcome);
        Assert.Equal(0, FakeResetAccountData.Of(fakeData).RecoveryCodeConsumeCount);

        // 2. Valid recovery code + current password throws ValidationException WITHOUT consuming the recovery code.
        var reuse = await Assert.ThrowsAsync<ValidationException>(() =>
            passwords.CompleteResetAsync(resetId, token, recoveryCode, currentPassword, currentPassword, "127.0.0.1"));
        Assert.Contains("not using now", reuse.Errors["NewPassword"][0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, FakeResetAccountData.Of(fakeData).RecoveryCodeConsumeCount);

        // 3. Correcting the password with the SAME recovery code succeeds and consumes it exactly once.
        var ok = await passwords.CompleteResetAsync(resetId, token, recoveryCode, newPassword, newPassword, "127.0.0.1");
        Assert.Equal(PasswordResetOutcome.Reset, ok.Outcome);
        Assert.Equal(1, FakeResetAccountData.Of(fakeData).RecoveryCodeConsumeCount);
    }

    [Fact]
    public async Task PasswordResetRejectsAlreadyConsumedTotpStepBeforePasswordReuseCheck()
    {
        var hasher = new IdentityPasswordHasher();
        var protector = new PassThroughSecretProtector();
        var codeHasher = new HmacOneTimeCodeHasher(RandomNumberGenerator.GetBytes(32));

        Guid userId = Guid.NewGuid();
        Guid resetId = Guid.NewGuid();
        string token = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        string currentPassword = "Current-Password-Long-Enough-99!";
        byte[] secretBytes = Totp.NewSecret();
        string totpSecret = Totp.ToBase32(secretBytes);
        long currentStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        string replayedCode = Totp.Code(secretBytes, currentStep);

        var user = new SignInUserRow
        {
            AppUserId = userId,
            UserName = "totpresetuser",
            DisplayName = "TOTP Reset User",
            PasswordHash = hasher.Hash(currentPassword),
            SecurityStamp = Guid.NewGuid(),
            MfaSecretProtected = protector.Protect(totpSecret),
            MfaEnabled = true,
            LastTotpStep = currentStep, // already consumed on this step
            IsActive = true,
        };

        var fakeData = FakeResetAccountData.Create(user, Guid.NewGuid(), hasher.Hash("UNUSED1234"));
        var accounts = new AccountService(fakeData, hasher, protector, new EmptyBreachedList(), new NullSignInEvents());
        var passwords = new PasswordService(fakeData, accounts, new FakeEmailSender(), codeHasher,
            Microsoft.Extensions.Options.Options.Create(new PasswordResetOptions()), new NullSignInEvents(), NullLogger<PasswordService>.Instance);

        // Submitting an already-consumed TOTP code with the current password must return WrongCode,
        // never revealing that NewPassword matches the current password.
        var outcome = await passwords.CompleteResetAsync(resetId, token, replayedCode, currentPassword, currentPassword, "127.0.0.1");
        Assert.Equal(PasswordResetOutcome.WrongCode, outcome.Outcome);
    }

    [Fact]
    public async Task SignOutClearsBrowserSessionAndLogsReasonWhenRevokeHitsConcurrencyConflict()
    {
        var recordedEvents = new List<SignInEvent>();
        var events = new RecordingSignInEvents(recordedEvents);
        var fakeData = FakeConcurrencyRevokeData.Create();
        var accounts = new AccountService(fakeData, new IdentityPasswordHasher(), new PassThroughSecretProtector(),
            new EmptyBreachedList(), events);

        var authService = new RecordingAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authService)
            .AddSingleton<IUrlHelperFactory, UrlHelperFactory>()
            .BuildServiceProvider();

        Guid scopeKey = Guid.NewGuid();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(MeritClaims.UserId, Guid.NewGuid().ToString()),
                new Claim(MeritClaims.ScopeKey, scopeKey.ToString()),
            ], AuthSchemes.Session)),
        };

        var controller = new AccountController(
            accounts, null!, null!, null!, null!, null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new EmailCodeOptions()),
            new ConfigurationBuilder().Build(),
            new StubHostEnvironment("Production"),
            events)
        {
            ControllerContext = new ControllerContext(new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor())),
        };

        var result = await controller.SignOut();

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AccountController.SignIn), redirect.ActionName);
        Assert.Equal(2, FakeConcurrencyRevokeData.Of(fakeData).RevokeAttempts);
        Assert.Contains(AuthSchemes.Session, authService.SignedOutSchemes);
        string setCookie = httpContext.Response.Headers.SetCookie.ToString();
        Assert.Contains("XSRF-TOKEN=", setCookie, StringComparison.Ordinal);
        var evt = Assert.Single(recordedEvents);
        Assert.Equal(SignInEventTypes.SignedOut, evt.Type);
        Assert.Equal("serverRevokeFailed", evt.Reason);
        Assert.Equal(scopeKey, evt.SessionScopeId);
    }

    private sealed class RecordingSignInEvents(List<SignInEvent> events) : ISignInEvents
    {
        public Task RecordAsync(SignInEvent e, CancellationToken ct = default)
        {
            events.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public List<string?> SignedOutSchemes { get; } = [];
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOutSchemes.Add(scheme);
            return Task.CompletedTask;
        }
    }

    private class FakeConcurrencyRevokeData : DispatchProxy
    {
        public int RevokeAttempts { get; private set; }

        public static IAccountData Create() => Create<IAccountData, FakeConcurrencyRevokeData>();
        public static FakeConcurrencyRevokeData Of(IAccountData proxy) => (FakeConcurrencyRevokeData)(object)proxy;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IAccountData.RevokeSessionScopeAsync) => FailRevoke(),
            _ => throw new NotImplementedException(targetMethod?.Name),
        };

        private Task FailRevoke()
        {
            RevokeAttempts++;
            return Task.FromException(new ConcurrencyException("The session could not be locked for revocation; please retry."));
        }
    }

    private sealed class PassThroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class EmptyBreachedList : IBreachedPasswordList
    {
        public bool IsBreached(string password) => false;
    }

    private sealed class NullSignInEvents : ISignInEvents
    {
        public Task RecordAsync(SignInEvent e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public bool IsConfigured => true;
        public Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default)
            => Task.FromResult(new EmailSendResult(true, EmailFailureKind.None, false, 1, null, 0));
    }

    private class FakeIntakeData : DispatchProxy
    {
        private IntakeBatchRow batch = null!;
        private IntakeProfileRow profile = null!;
        private Action<string, string?> onStatus = null!;

        public static IIntakeData Create(IntakeBatchRow batch, IntakeProfileRow profile, Action<string, string?> onStatus)
        {
            var proxy = Create<IIntakeData, FakeIntakeData>();
            var impl = (FakeIntakeData)(object)proxy;
            impl.batch = batch;
            impl.profile = profile;
            impl.onStatus = onStatus;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IIntakeData.GetBatchAsync) => Task.FromResult<IntakeBatchRow?>(batch),
            nameof(IIntakeData.GetProfileAsync) => Task.FromResult<IntakeProfileRow?>(profile),
            nameof(IIntakeData.SetBatchStatusAsync) => RecordStatus((string)args![1]!, (string?)args[2]),
            _ => throw new NotImplementedException(targetMethod?.Name),
        };

        private Task RecordStatus(string status, string? message)
        {
            onStatus(status, message);
            return Task.CompletedTask;
        }
    }

    private class FakeResetAccountData : DispatchProxy
    {
        private SignInUserRow user = null!;
        private Guid recoveryId;
        private string recoveryHash = string.Empty;
        private bool recoveryUsed;

        public int RecoveryCodeConsumeCount { get; private set; }

        public static IAccountData Create(SignInUserRow user, Guid recoveryId, string recoveryHash)
        {
            var proxy = Create<IAccountData, FakeResetAccountData>();
            var impl = (FakeResetAccountData)(object)proxy;
            impl.user = user;
            impl.recoveryId = recoveryId;
            impl.recoveryHash = recoveryHash;
            return proxy;
        }

        public static FakeResetAccountData Of(IAccountData proxy) => (FakeResetAccountData)(object)proxy;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IAccountData.GetPasswordResetAsync) => Task.FromResult(new PasswordResetRow
            {
                Status = "valid",
                UserName = user.UserName,
                MfaEnabled = true,
                PasswordMinLength = 15,
                RemainingAttempts = 5,
            }),
            nameof(IAccountData.GetUserForSignInAsync) => Task.FromResult<SignInUserRow?>(user),
            nameof(IAccountData.GetRecoveryCodesAsync) => Task.FromResult<IReadOnlyList<RecoveryCodeRow>>(
                recoveryUsed ? [] : [new RecoveryCodeRow { MfaRecoveryCodeId = recoveryId, CodeHash = recoveryHash }]),
            nameof(IAccountData.UseRecoveryCodeAsync) => ConsumeRecovery((Guid)args![0]!),
            nameof(IAccountData.RecordSignInResultAsync) => Task.CompletedTask,
            nameof(IAccountData.RecordPasswordResetFailureAsync) => Task.FromResult(4),
            nameof(IAccountData.CompletePasswordResetAsync) => Task.FromResult<Guid?>(user.AppUserId),
            _ => throw new NotImplementedException(targetMethod?.Name),
        };

        private Task<bool> ConsumeRecovery(Guid id)
        {
            if (recoveryUsed || id != recoveryId) return Task.FromResult(false);
            recoveryUsed = true;
            RecoveryCodeConsumeCount++;
            return Task.FromResult(true);
        }
    }
}
