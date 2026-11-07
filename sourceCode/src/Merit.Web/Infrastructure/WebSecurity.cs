using System.Globalization;
using System.Security.Claims;
using Merit.Application.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Merit.Web.Infrastructure;

/// <summary>Claim types MERIT puts in the session cookie. No patient data is ever placed in a token or claim.</summary>
public static class MeritClaims
{
    public const string UserId = "merit:uid";
    public const string UserName = "merit:uname";
    public const string DisplayName = "merit:name";
    public const string ScopeKey = "merit:scope";
    public const string RootTenantId = "merit:root";
    public const string ActingTenantId = "merit:tenant";
    public const string ActingTenantName = "merit:tenantName";
    public const string SecurityStamp = "merit:stamp";
    public const string Permission = "merit:perm";
    public const string MfaStep = "merit:mfaStep";
    public const string EnrollSecret = "merit:enrollSecret";
    public const string IsAdministrator = "merit:admin";
    public const string ProfileId = "merit:profile";
    public const string ProfileName = "merit:profileName";
    public const string TimeZone = "merit:tz";
    public const string ViewAsUserId = "merit:viewAs";
    public const string ViewAsName = "merit:viewAsName";
    public const string IsPlatformAdmin = "merit:platform";
    /// <summary>"1" while an administrator's request for a new password is outstanding (#190).</summary>
    public const string PasswordChangeRequired = "merit:pwchange";
}

public static class AuthSchemes
{
    /// <summary>The signed-in session (after both factors).</summary>
    public const string Session = "Merit";

    /// <summary>A password-verified user who still owes a second factor; lasts ten minutes.</summary>
    public const string PendingMfa = "MeritMfa";
}

/// <summary>ICurrentSession for web requests, read from the session cookie's claims.</summary>
public sealed class WebCurrentSession(IHttpContextAccessor accessor) : ICurrentSession
{
    private IReadOnlySet<string>? permissions;

    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true
                                   && User.Identity.AuthenticationType == AuthSchemes.Session;

    public Guid? UserId => Guid(MeritClaims.UserId);

    public string? UserName => IsAuthenticated ? User?.FindFirstValue(MeritClaims.UserName) : null;

    public string? DisplayName => IsAuthenticated ? User?.FindFirstValue(MeritClaims.DisplayName) : null;

    public Guid? ScopeKey => Guid(MeritClaims.ScopeKey);

    public Guid? RootTenantId => Guid(MeritClaims.RootTenantId);

    public Guid? ActingTenantId => Guid(MeritClaims.ActingTenantId);

    public string? ActingTenantName => IsAuthenticated ? User?.FindFirstValue(MeritClaims.ActingTenantName) : null;

    public string? ProfileName => IsAuthenticated ? User?.FindFirstValue(MeritClaims.ProfileName) : null;

    public string TimeZoneId => (IsAuthenticated ? User?.FindFirstValue(MeritClaims.TimeZone) : null) ?? JurisdictionTimeZones.Default;

    public Guid? ProfileId => Guid(MeritClaims.ProfileId);

    public Guid? ViewAsUserId => Guid(MeritClaims.ViewAsUserId);

    public string? ViewAsDisplayName => IsAuthenticated ? User?.FindFirstValue(MeritClaims.ViewAsName) : null;

    public bool IsPlatformAdmin => IsAuthenticated && User?.FindFirstValue(MeritClaims.IsPlatformAdmin) == "1";

    public IReadOnlySet<string> Permissions => permissions ??= IsAuthenticated
        ? User!.FindAll(MeritClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    private Guid? Guid(string type)
        => IsAuthenticated && System.Guid.TryParse(User?.FindFirstValue(type), CultureInfo.InvariantCulture, out var g) ? g : null;
}

/// <summary>ASP.NET Core Identity's password hasher (PBKDF2, V3 format), without the rest of Identity.</summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    public const int IterationCount = 220_000;
    public const string FormatPrefix = "merit-nfc-v1$";
    private readonly PasswordHasher<object> inner = new(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = IterationCount,
    }));
    private static readonly object User = new();

    public string Hash(string password) => FormatPrefix + inner.HashPassword(User, PasswordPolicy.Normalize(password));

    public PasswordCheck Verify(string hash, string password)
    {
        try
        {
            bool normalized = hash.StartsWith(FormatPrefix, StringComparison.Ordinal);
            // Unmarked credentials retain their original byte representation until a successful verification upgrades them.
            string canonical = PasswordPolicy.Normalize(password);
            string candidate = normalized ? canonical : password;
            if (candidate.Length > PasswordPolicy.MaximumLength * 2) return PasswordCheck.Failed;
            return inner.VerifyHashedPassword(User, normalized ? hash[FormatPrefix.Length..] : hash, candidate) switch
            {
                PasswordVerificationResult.Success => normalized ? PasswordCheck.Success : PasswordCheck.SuccessRehashNeeded,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
                _ => PasswordCheck.Failed,
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return PasswordCheck.Failed;
        }
    }
}

/// <summary>Encrypts TOTP secrets at rest with ASP.NET Core data protection.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Merit.MfaSecret.v1");

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string protectedText) => protector.Unprotect(protectedText);
}

/// <summary>
/// A local list of the most common breached passwords (NIST SP 800-63B-4 requires checking new passwords against known
/// compromised values). An adopter can replace this with a larger corpus or a k-anonymity range service.
/// </summary>
public sealed class CommonBreachedPasswordList : IBreachedPasswordList
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase);

    static CommonBreachedPasswordList()
    {
        foreach (string name in new[] { "common-passwords.txt", "merit-passwords.txt" })
        {
            using var stream = typeof(CommonBreachedPasswordList).Assembly.GetManifestResourceStream($"Merit.Web.Security.{name}")
                               ?? throw new InvalidOperationException("A bundled password blocklist is missing.");
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) Common.Add(PasswordPolicy.Normalize(line.Trim()));
            }
        }
    }

    public bool IsBreached(string password) => Common.Contains(PasswordPolicy.Normalize(password).Trim());
}
