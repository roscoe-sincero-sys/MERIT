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
    public const string ViewAsUserId = "merit:viewAs";
    public const string ViewAsName = "merit:viewAsName";
    public const string IsPlatformAdmin = "merit:platform";
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
    private readonly PasswordHasher<object> inner = new();
    private static readonly object User = new();

    public string Hash(string password) => inner.HashPassword(User, password);

    public PasswordCheck Verify(string hash, string password)
    {
        try
        {
            return inner.VerifyHashedPassword(User, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
                _ => PasswordCheck.Failed,
            };
        }
        catch (FormatException)
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
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "123456", "12345678", "123456789", "1234567890", "qwerty", "qwerty123",
        "abc123", "111111", "123123", "letmein", "welcome", "welcome1", "monkey", "dragon", "football", "baseball",
        "iloveyou", "admin", "admin123", "administrator", "passw0rd", "p@ssw0rd", "p@ssword", "changeme", "sunshine",
        "princess", "trustno1", "master", "superman", "batman", "starwars", "whatever", "freedom", "shadow", "michael",
        "jennifer", "hunter2", "summer2024", "winter2024", "spring2025", "fall2025", "summer2025", "winter2025",
        "summer2026", "winter2026", "maryland", "maryland1", "baltimore", "health123", "merit", "merit123", "lead1234",
        "qwertyuiop", "1q2w3e4r", "zaq12wsx", "asdfghjkl", "000000", "654321", "987654321", "11111111", "88888888",
    };

    public bool IsBreached(string password) => Common.Contains(password.Trim());
}
