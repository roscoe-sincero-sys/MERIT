using Merit.Application.Data;

namespace Merit.Application.Services;

/// <summary>Shared labels for type-ahead results and selected identifiers redisplayed after validation.</summary>
public static class LookupLabels
{
    public static string Person(string lastName, string? firstName) => $"{lastName}, {firstName}";
    public static string Provider(string lastName, string? firstName, string? credential) => $"{lastName}, {firstName} {credential}".Trim();
    public static string Location(string? street, string? unit) => $"{street}{(string.IsNullOrEmpty(unit) ? string.Empty : " " + unit)}";

    public static string Format(LookupSelectionRow row) => row.Kind switch
    {
        "person" => Person(row.Name, row.FirstName),
        "provider" => Provider(row.Name, row.FirstName, row.Credential),
        "location" => Location(row.Name, row.Unit),
        _ => row.Name,
    };
}
