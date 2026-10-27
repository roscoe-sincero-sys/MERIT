namespace Merit.Application.Security;

/// <summary>
/// "Today" and local times for the session's jurisdiction (issue #36). Instants are stored in UTC; anything a person reads
/// or that is dated "today" (an activity, a case opened, a default form date, a report period) follows the time zone of
/// the session's data root, so evening work in the United States is not dated tomorrow.
/// </summary>
public interface IClock
{
    /// <summary>Today's date in the jurisdiction's time zone.</summary>
    DateOnly Today { get; }

    /// <summary>The jurisdiction's time zone.</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>A UTC instant as the jurisdiction's wall-clock time.</summary>
    DateTime ToLocal(DateTime utc);
}

public sealed class JurisdictionClock(ICurrentSession session, TimeProvider time) : IClock
{
    public TimeZoneInfo Zone => JurisdictionTimeZones.Find(session.TimeZoneId);

    public DateOnly Today => DateOnly.FromDateTime(ToLocal(time.GetUtcNow().UtcDateTime));

    public DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}

/// <summary>
/// The time zones a data root can choose: Windows names, as SQL Server's AT TIME ZONE and sys.time_zone_info use them
/// (.NET maps them to IANA names on Linux). A root with none set is on Eastern time.
/// </summary>
public static class JurisdictionTimeZones
{
    public const string Default = "Eastern Standard Time";

    public static readonly IReadOnlyList<(string Id, string Label)> Choices =
    [
        ("Eastern Standard Time", "Eastern"),
        ("Central Standard Time", "Central"),
        ("Mountain Standard Time", "Mountain"),
        ("US Mountain Standard Time", "Mountain, no daylight saving (Arizona)"),
        ("Pacific Standard Time", "Pacific"),
        ("Alaskan Standard Time", "Alaska"),
        ("Hawaiian Standard Time", "Hawaii"),
        ("SA Western Standard Time", "Atlantic (Puerto Rico, U.S. Virgin Islands)"),
        ("West Pacific Standard Time", "Chamorro (Guam, Northern Mariana Islands)"),
        ("UTC-11", "Samoa (American Samoa)"),
    ];

    public static bool IsKnown(string? id) => id is not null && Choices.Any(c => c.Id == id);

    /// <summary>The zone for a stored id; the default when the id is missing or unknown on this machine.</summary>
    public static TimeZoneInfo Find(string? id)
    {
        foreach (var candidate in new[] { id, Default })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.Utc;
    }

    /// <summary>The IANA name, for the browser (Intl.DateTimeFormat).</summary>
    public static string IanaId(string? id)
    {
        var zone = Find(id);
        return zone.HasIanaId ? zone.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "America/New_York";
    }
}
