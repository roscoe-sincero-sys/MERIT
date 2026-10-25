using System.Globalization;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Merit.Domain.Residence;
using Merit.Domain.Text;

namespace Merit.Application.Services;

/// <summary>An address typed by a user or carried on a report.</summary>
public sealed record AddressInput(string? AddressLine1, string? Unit, string? City, string? State, string? Zip5,
                                  string Country = "US", string? CountyFips = null, bool IsCityOnly = false,
                                  int? YearBuilt = null, string? HousingType = null, string? Tenure = null);

/// <summary>A place to add for a person: an existing location, an organization site, or a new address.</summary>
public sealed record AddPlaceInput(
    Guid PersonId,
    string Role,
    Guid? LocationId,
    Guid? OrganizationSiteId,
    AddressInput? Address,
    string? ScheduleType,
    decimal? HoursPerWeek,
    string? ScheduleNote,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Source,
    bool? NewIsPrimary,
    string? PrimaryReason);

/// <summary>
/// Places: residences, child care, school and other regular sites. The rules live in Merit.Domain.Residence and are
/// enforced again by dbo.uspSavePersonLocations under row locks; this service turns user intent into a plan, shows it,
/// and applies exactly the plan that was shown.
/// </summary>
public sealed class PlaceService(IPlaceData places, IPersonData persons, ICurrentSession session)
{
    public static PlaceRow ToPlaceRow(PlaceListRow r) => new(
        r.PersonLocationId, r.LocationId, r.OrganizationSiteId, r.Role, r.IsPrimary, r.PrimaryReason, r.ScheduleType,
        r.HoursPerWeek, r.ScheduleNote, r.StartDate, r.EndDate, r.Source, r.VerifiedDate, ToHex(r.RowVersion), r.CountyFips,
        r.CountyName, r.OrganizationName is null ? r.AddressLabel : $"{r.OrganizationName} ({r.AddressLabel})");

    public async Task<IReadOnlyList<PlaceListRow>> GetPlacesAsync(Guid personId, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        return await persons.GetPlacesAsync(personId, ct).ConfigureAwait(false);
    }

    /// <summary>The "Set as primary" dialog: the row split and the county change, computed by the server rule.</summary>
    public async Task<PlacePlan> PreviewSetPrimaryAsync(Guid personId, Guid personLocationId, DateOnly startDate, string? reason,
                                                       bool isAdult, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var rows = (await persons.GetPlacesAsync(personId, ct).ConfigureAwait(false)).Select(ToPlaceRow).ToList();
        return PrimaryResidencePlanner.PlanSetPrimary(rows, personLocationId, startDate, reason, TimeOrderedId.New, isAdult);
    }

    public async Task<PlacePlan> SetPrimaryAsync(Guid personId, Guid personLocationId, DateOnly startDate, string? reason,
                                                bool isAdult, CancellationToken ct = default)
    {
        var plan = await PreviewSetPrimaryAsync(personId, personLocationId, startDate, reason, isAdult, ct).ConfigureAwait(false);
        await ApplyAsync(personId, plan, ct).ConfigureAwait(false);
        return plan;
    }

    /// <summary>Plans adding a place, resolving the address to a shared location first.</summary>
    public async Task<(PlacePlan Plan, Guid LocationId)> PlanAddPlaceAsync(AddPlaceInput input, bool isAdult, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var v = new Validator();
        v.When(input.Role is not ("residence" or "childCare" or "school" or "relativeHome" or "otherSite"), "Role", "Choose a role.");
        v.When(input.LocationId is null && input.Address is null, "AddressLine1", "Enter an address or pick an existing place.");
        v.When(input.EndDate is not null && input.EndDate < input.StartDate, "EndDate", "The end date is before the start date.");
        v.When(input.HoursPerWeek is < 0 or > 168, "HoursPerWeek", "Hours per week must be between 0 and 168.");
        if (input.Address is { } a && input.LocationId is null)
        {
            ValidateAddress(a, v);
        }

        v.ThrowIfInvalid();

        Guid locationId = input.LocationId ?? await SaveAddressAsync(input.Address!, ct).ConfigureAwait(false);
        var rows = (await persons.GetPlacesAsync(input.PersonId, ct).ConfigureAwait(false)).Select(ToPlaceRow).ToList();
        string label = input.Address is { } addr ? FormatAddress(addr) : "The new place";
        var newRow = new PlaceRow(TimeOrderedId.New(), locationId, input.OrganizationSiteId, input.Role, false, null,
            input.ScheduleType, input.HoursPerWeek, input.ScheduleNote, input.StartDate, input.EndDate, input.Source, null, null,
            input.Address?.CountyFips, null, label);
        return (PrimaryResidencePlanner.PlanAddPlace(rows, newRow, input.NewIsPrimary, input.PrimaryReason, TimeOrderedId.New, isAdult),
            locationId);
    }

    public async Task<PlacePlan> AddPlaceAsync(AddPlaceInput input, bool isAdult, CancellationToken ct = default)
    {
        var (plan, _) = await PlanAddPlaceAsync(input, isAdult, ct).ConfigureAwait(false);
        await ApplyAsync(input.PersonId, plan, ct).ConfigureAwait(false);
        return plan;
    }

    /// <summary>Ends a place on a date (the person no longer lives or spends time there).</summary>
    public async Task EndPlaceAsync(Guid personId, Guid personLocationId, DateOnly endDate, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var rows = (await persons.GetPlacesAsync(personId, ct).ConfigureAwait(false)).Select(ToPlaceRow).ToList();
        var row = rows.FirstOrDefault(r => r.PersonLocationId == personLocationId) ?? throw new NotFoundException();
        if (endDate < row.StartDate)
        {
            throw new ValidationException("EndDate", "The end date is before the start date.");
        }

        var ended = row with { EndDate = endDate };
        var plan = new PlacePlan([new PlaceChange(ChangeKind.Update, ended, "Place ended.")], [], [], null, null);
        await ApplyAsync(personId, plan, ct).ConfigureAwait(false);
    }

    /// <summary>Marks a lab-reported place verified by staff.</summary>
    public async Task VerifyPlaceAsync(Guid personId, Guid personLocationId, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var rows = (await persons.GetPlacesAsync(personId, ct).ConfigureAwait(false)).Select(ToPlaceRow).ToList();
        var row = rows.FirstOrDefault(r => r.PersonLocationId == personLocationId) ?? throw new NotFoundException();
        var verified = row with { VerifiedDate = DateOnly.FromDateTime(DateTime.UtcNow) };
        await ApplyAsync(personId, new PlacePlan([new PlaceChange(ChangeKind.Update, verified, "Verified.")], [], [], null, null), ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LocationCandidateRow>> FindLocationsAsync(string? text, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        string prefix = MatchKeys.AddressSearchPrefix(text);
        return prefix.Length < 3 ? [] : await places.FindLocationsAsync(prefix, null, 10, ct).ConfigureAwait(false);
    }

    /// <summary>Finds or creates the shared location for an address.</summary>
    public async Task<Guid> SaveAddressAsync(AddressInput a, CancellationToken ct = default)
    {
        string key = MatchKeys.AddressKey(a.AddressLine1, a.Unit, a.City, a.State, a.Zip5);
        string? building = string.IsNullOrWhiteSpace(a.Unit) ? null : MatchKeys.BuildingKey(a.AddressLine1, a.City, a.State, a.Zip5);
        var saved = await places.SaveLocationAsync(new LocationSave(TimeOrderedId.New(), a.Country, Clean(a.AddressLine1),
            Clean(a.Unit), Clean(a.City) ?? string.Empty, Clean(a.State)?.ToUpperInvariant(), Clean(a.Zip5), null, null,
            Clean(a.CountyFips), a.IsCityOnly, key, building, a.YearBuilt, a.HousingType, a.Tenure), ct).ConfigureAwait(false);
        return saved.LocationId;
    }

    public static void ValidateAddress(AddressInput a, Validator v)
    {
        v.Required("City", a.City, "City");
        if (a.Country == "US" && !a.IsCityOnly)
        {
            v.Required("AddressLine1", a.AddressLine1, "Street address");
            v.When(string.IsNullOrWhiteSpace(a.State) || a.State.Trim().Length != 2, "State", "Enter the two-letter state code.");
            v.When(string.IsNullOrWhiteSpace(a.Zip5) || a.Zip5.Trim().Length != 5 || !a.Zip5.Trim().All(char.IsAsciiDigit),
                "Zip5", "Enter a five-digit ZIP code.");
        }

        v.When(a.CountyFips is { Length: > 0 } f && (f.Length != 3 || !f.All(char.IsAsciiDigit)), "CountyFips",
            "County is a three-digit FIPS code.");
        v.When(a.YearBuilt is int y && (y < 1700 || y > DateTime.UtcNow.Year), "YearBuilt",
            $"Year built must be between 1700 and {DateTime.UtcNow.Year}.");
    }

    public static string FormatAddress(AddressInput a)
        => a.IsCityOnly
            ? $"{a.City} (city only)"
            : string.Join(", ", new[] { a.AddressLine1 + (string.IsNullOrWhiteSpace(a.Unit) ? string.Empty : " " + a.Unit), a.City, a.State }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

    private async Task ApplyAsync(Guid personId, PlacePlan plan, CancellationToken ct)
    {
        if (!plan.IsValid)
        {
            throw new BusinessRuleException(string.Join(" ", plan.Errors));
        }

        await places.SavePersonLocationsAsync(personId, ToChangesJson(plan), ct).ConfigureAwait(false);
    }

    public static string ToChangesJson(PlacePlan plan)
    {
        var items = plan.Changes.Select(c => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["personLocationId"] = c.Row.PersonLocationId,
            ["locationId"] = c.Row.LocationId,
            ["organizationSiteId"] = c.Row.OrganizationSiteId,
            ["role"] = c.Row.Role,
            ["isPrimary"] = c.Row.IsPrimary,
            ["primaryReason"] = c.Row.PrimaryReason,
            ["scheduleType"] = c.Row.ScheduleType,
            ["hoursPerWeek"] = c.Row.HoursPerWeek,
            ["scheduleNote"] = c.Row.ScheduleNote,
            ["startDate"] = c.Row.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = c.Row.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["source"] = c.Row.Source,
            ["verifiedDate"] = c.Row.VerifiedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["isDeleted"] = false,
            ["rowVersion"] = c.Kind == ChangeKind.Update ? c.Row.RowVersionHex : null,
        });
        return JsonSerializer.Serialize(items);
    }

    public static string? ToHex(byte[]? rowVersion)
        => rowVersion is null || rowVersion.Length == 0 ? null : "0x" + Convert.ToHexString(rowVersion);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
