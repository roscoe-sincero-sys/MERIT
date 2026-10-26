namespace Merit.Domain.Tenants;

/// <summary>
/// Where each kind of tenant may sit in the tree. Regions, states, tribes and stand-alone jurisdictions are data roots
/// (they own records); programs and local jurisdictions sit under a data root or under each other and share its records.
/// </summary>
public static class TenantRules
{
    public static readonly IReadOnlyList<string> RootTypes = ["region", "state", "tribe", "jurisdiction"];

    /// <summary>The tenant types that may be added under a parent of the given type.</summary>
    public static IReadOnlyList<string> ChildTypes(string parentType) => parentType switch
    {
        "platform" => RootTypes,
        "region" => ["state", "tribe"],
        "state" or "tribe" or "jurisdiction" or "program" => ["program", "jurisdiction"],
        _ => [],
    };

    /// <summary>A new tenant under this parent is its own data root (new records, roles and settings).</summary>
    public static bool CreatesDataRoot(string parentType) => parentType is "platform" or "region";

    /// <summary>
    /// Whether a new tenant under this parent may serve any county of the data root and add new ones. Below the data
    /// root a tenant serves only counties its parent serves, and new counties are added under the root: counties give
    /// residence access to the people who live there.
    /// </summary>
    public static bool MayChooseAnyCounty(bool parentIsDataRoot, string parentType)
        => parentIsDataRoot || CreatesDataRoot(parentType);

    /// <summary>States and tribes carry a two-letter postal code (ABLES StateRep); FIPS is optional for tribes.</summary>
    public static bool NeedsPostalCode(string tenantType) => tenantType is "state" or "tribe";

    /// <summary>Capital letters, digits and hyphens, starting with a letter or digit, up to 30 characters.</summary>
    public static bool IsValidCode(string? code)
        => !string.IsNullOrEmpty(code) && code.Length <= 30 && char.IsAsciiLetterOrDigit(code[0])
           && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>A county FIPS code is the three digits after the state's two.</summary>
    public static bool IsValidCountyFips(string? fips) => fips is { Length: 3 } && fips.All(char.IsAsciiDigit);

    /// <summary>Splits "005, 510 033" into distinct codes, in the order given.</summary>
    public static IReadOnlyList<string> ParseFipsList(string? text)
        => (text ?? string.Empty).Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToList();
}
