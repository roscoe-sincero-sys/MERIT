namespace Merit.Web.Infrastructure;

/// <summary>
/// The time zone the current request shows times in: the session's jurisdiction (issue #36). Set once per request by the
/// middleware in Program.cs, so the static view helpers (<see cref="Options.Date(DateTime?)"/>) need no parameter.
/// </summary>
public static class DisplayTime
{
    private static readonly AsyncLocal<TimeZoneInfo?> Current = new();

    public static TimeZoneInfo Zone
    {
        get => Current.Value ?? Merit.Application.Security.JurisdictionTimeZones.Find(null);
        set => Current.Value = value;
    }

    public static DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}
