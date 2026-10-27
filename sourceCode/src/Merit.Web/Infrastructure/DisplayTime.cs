namespace Merit.Web.Infrastructure;

/// <summary>
/// The time zone the current request shows times in: the session's jurisdiction (issue #36). Set once per request by the
/// middleware in Program.cs, so the static view helpers (<see cref="Options.Date(DateTime?)"/>) need no parameter.
/// </summary>
public static class DisplayTime
{
    private static readonly AsyncLocal<TimeZoneInfo?> Current = new();
    private static readonly AsyncLocal<Merit.Application.Reference.DisplayNames?> CurrentNames = new();

    /// <summary>The words for stored codes on this request's pages (issue #37); null before sign-in.</summary>
    public static Merit.Application.Reference.DisplayNames? Names
    {
        get => CurrentNames.Value;
        set => CurrentNames.Value = value;
    }

    public static TimeZoneInfo Zone
    {
        get => Current.Value ?? Merit.Application.Security.JurisdictionTimeZones.Find(null);
        set => Current.Value = value;
    }

    public static DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}
