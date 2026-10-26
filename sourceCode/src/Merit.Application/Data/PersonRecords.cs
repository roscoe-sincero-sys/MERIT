namespace Merit.Application.Data;

// Rows returned by the person, place and organization procedures.

public sealed class SearchPersonRow
{
    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string? MiddleName { get; init; }

    public DateOnly? Dob { get; init; }

    public string Sex { get; init; } = "unknown";

    public string MatchedOn { get; init; } = string.Empty;

    public string? CurrentCounty { get; init; }

    /// <summary>The person's open cases as uspSearch returns them: a JSON array of the fields of <see cref="OpenCaseChipRow"/>.</summary>
    public string? OpenCasesJson { get; init; }

    /// <summary>The open cases, shown as the same chips as the person page header (issue #19).</summary>
    public IReadOnlyList<OpenCaseChipRow> OpenCases => string.IsNullOrEmpty(OpenCasesJson)
        ? []
        : System.Text.Json.JsonSerializer.Deserialize<List<OpenCaseChipRow>>(OpenCasesJson, CaseJson) ?? [];

    private static readonly System.Text.Json.JsonSerializerOptions CaseJson = new() { PropertyNameCaseInsensitive = true };
}

public sealed class SearchCaseRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public Guid PersonId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string CaseStatus { get; init; } = string.Empty;

    public DateOnly OpenedDate { get; init; }
}

public sealed class SearchOrganizationRow
{
    public Guid OrganizationId { get; init; }

    public string OrganizationName { get; init; } = string.Empty;

    public string? NaicsCode { get; init; }

    public string? CliaNumber { get; init; }

    public string? OrganizationTypes { get; init; }
}

public sealed record SearchResults(IReadOnlyList<SearchPersonRow> Persons, IReadOnlyList<SearchCaseRow> Cases,
                                   IReadOnlyList<SearchOrganizationRow> Organizations, bool ExistsElsewhere);

public sealed class PersonRow
{
    public Guid PersonId { get; init; }

    public Guid RootTenantId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string? MiddleName { get; init; }

    public DateOnly? Dob { get; init; }

    public bool DobVerified { get; init; }

    public string Sex { get; init; } = "unknown";

    public string Ethnicity { get; init; } = "unknown";

    public string? PreferredLanguage { get; init; }

    public DateOnly? DeceasedDate { get; init; }

    public Guid? MergedIntoPersonId { get; init; }

    public bool IsPlaceholderName { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public string? CurrentCountyFips { get; init; }

    public string? CurrentCountyName { get; init; }

    public Guid? CurrentPrimaryLocationId { get; init; }

    public bool IsContactOnly { get; init; }

    public bool HasPregnancyEpisodes { get; init; }
}

public sealed class AliasRow
{
    public Guid PersonAliasId { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string? MiddleName { get; init; }

    public string AliasType { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateOnly FirstSeen { get; init; }

    public DateOnly LastSeen { get; init; }
}

public sealed class RaceRow
{
    public string RaceCode { get; init; } = string.Empty;

    public string? DisplayName { get; init; }
}

public sealed class ContactMethodRow
{
    public Guid PersonContactMethodId { get; init; }

    public string ContactType { get; init; } = string.Empty;

    public string ContactValue { get; init; } = string.Empty;

    public bool IsPreferred { get; init; }
}

public sealed class IdentifierRow
{
    public Guid PersonIdentifierId { get; init; }

    public string IdentifierType { get; init; } = string.Empty;

    public string IdentifierValue { get; init; } = string.Empty;

    public string? AssigningFacility { get; init; }

    public string Source { get; init; } = string.Empty;

    public DateOnly ReportedDate { get; init; }
}

public sealed class OpenCaseChipRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public string AnalyteCode { get; init; } = string.Empty;

    public string AnalyteName { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string CaseStatus { get; init; } = string.Empty;

    public string Population { get; init; } = string.Empty;
}

public sealed record PersonHeader(
    PersonRow Person,
    IReadOnlyList<AliasRow> Aliases,
    IReadOnlyList<RaceRow> Races,
    IReadOnlyList<ContactMethodRow> ContactMethods,
    IReadOnlyList<IdentifierRow> Identifiers,
    IReadOnlyList<OpenCaseChipRow> OpenCases);

public sealed class RelationshipRow
{
    public Guid PersonRelationshipId { get; init; }

    public bool IsInverse { get; init; }

    public Guid OtherPersonId { get; init; }

    public string OtherLastName { get; init; } = string.Empty;

    public string? OtherFirstName { get; init; }

    public DateOnly? OtherDob { get; init; }

    public string Role { get; init; } = string.Empty;

    public string RoleLabel { get; init; } = string.Empty;

    public bool IsPrimaryContact { get; init; }

    public bool HasLegalCustody { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public string Source { get; init; } = string.Empty;

    public DateOnly? VerifiedDate { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public bool OtherIsContactOnly { get; init; }
}

public sealed class PlaceListRow
{
    public Guid PersonLocationId { get; init; }

    public Guid LocationId { get; init; }

    public Guid? OrganizationSiteId { get; init; }

    public string? SiteName { get; init; }

    public string? OrganizationName { get; init; }

    public string Role { get; init; } = string.Empty;

    public bool IsPrimary { get; init; }

    public string? PrimaryReason { get; init; }

    public string? ScheduleType { get; init; }

    public decimal? HoursPerWeek { get; init; }

    public string? ScheduleNote { get; init; }

    public DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public string Source { get; init; } = string.Empty;

    public DateOnly? VerifiedDate { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public string? AddressLine1 { get; init; }

    public string? Unit { get; init; }

    public string City { get; init; } = string.Empty;

    public string? State { get; init; }

    public string? Zip5 { get; init; }

    public string Country { get; init; } = "US";

    public string? CountyFips { get; init; }

    public string? CountyName { get; init; }

    public int? YearBuilt { get; init; }

    public bool IsCityOnly { get; init; }

    public string AddressLabel
        => IsCityOnly
            ? $"{City} (city only)"
            : string.Join(", ", new[] { AddressLine1 + (string.IsNullOrEmpty(Unit) ? string.Empty : " " + Unit), City, State }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class EmploymentRow
{
    public Guid EmploymentId { get; init; }

    public Guid OrganizationId { get; init; }

    public string OrganizationName { get; init; } = string.Empty;

    public string? OrganizationNaics { get; init; }

    public Guid? WorksiteLocationId { get; init; }

    public string? WorksiteAddress { get; init; }

    public string? WorksiteCity { get; init; }

    public string? WorksiteState { get; init; }

    public string? JobTitle { get; init; }

    public string? IndustryText { get; init; }

    public string? OccupationCode { get; init; }

    public string? NaicsOverride { get; init; }

    public string? MetalTasks { get; init; }

    public string? PpeNotes { get; init; }

    public decimal? HoursPerWeek { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public string Source { get; init; } = string.Empty;

    public DateOnly? VerifiedDate { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public string? Analytes { get; init; }
}

public sealed class ExposureSourceRow
{
    public Guid ExposureSourceId { get; init; }

    public Guid? ExposureCaseId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string SourceTypeLabel { get; init; } = string.Empty;

    public string? SourceDescription { get; init; }

    public Guid? ProductId { get; init; }

    public string? ProductName { get; init; }

    public string? Brand { get; init; }

    public Guid? LocationId { get; init; }

    public string? AddressLine1 { get; init; }

    public string? City { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public bool IsConfirmed { get; init; }
}

public sealed class PregnancyRow
{
    public Guid PregnancyEpisodeId { get; init; }

    public DateOnly StartDate { get; init; }

    public DateOnly? ExpectedEndDate { get; init; }

    public DateOnly? ActualEndDate { get; init; }

    public string? Outcome { get; init; }

    public string Source { get; init; } = string.Empty;

    public Guid? NewbornPersonId { get; init; }

    public string? NewbornLastName { get; init; }

    public string? NewbornFirstName { get; init; }

    public DateOnly? NewbornDob { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class PersonCaseRow
{
    public Guid ExposureCaseId { get; init; }

    public string CaseNumber { get; init; } = string.Empty;

    public string AnalyteCode { get; init; } = string.Empty;

    public string AnalyteName { get; init; } = string.Empty;

    public string Population { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string ComputedClassification { get; init; } = string.Empty;

    public string CaseStatus { get; init; } = string.Empty;

    public string ExposureCategory { get; init; } = string.Empty;

    public string WorkflowTemplate { get; init; } = string.Empty;

    public DateOnly OpenedDate { get; init; }

    public DateOnly? ClosedDate { get; init; }

    public string? ClosureReason { get; init; }

    public string? AssignedToName { get; init; }

    public string? OwningTenantName { get; init; }

    public decimal? HighestValue { get; init; }

    public string? HighestUnit { get; init; }

    public int OpenTasks { get; init; }

    public int Investigations { get; init; }
}

public sealed class PersonResultRow
{
    public Guid LabResultId { get; init; }

    public Guid AnalyteSpecimenId { get; init; }

    public Guid AnalyteId { get; init; }

    public string AnalyteCode { get; init; } = string.Empty;

    public string AnalyteName { get; init; } = string.Empty;

    public string Specimen { get; init; } = string.Empty;

    public DateOnly CollectedDate { get; init; }

    public DateOnly? ReportedDate { get; init; }

    public DateTime ReceivedUtc { get; init; }

    public string ResultStatus { get; init; } = string.Empty;

    public string Operator { get; init; } = "=";

    public decimal? ValueReported { get; init; }

    public string? ValueText { get; init; }

    public string UnitReported { get; init; } = string.Empty;

    public decimal? ValueCanonical { get; init; }

    public string UnitCanonical { get; init; } = string.Empty;

    public string? SampleType { get; init; }

    public string? TestMethod { get; init; }

    public string? Speciation { get; init; }

    public string Population { get; init; } = string.Empty;

    public bool IsIndeterminate { get; init; }

    public string ResultRowStatus { get; init; } = string.Empty;

    public string? AccessionNumber { get; init; }

    public string? LabName { get; init; }

    public string? OrderingProviderName { get; init; }
}

public sealed class SavePersonResult
{
    public Guid PersonId { get; init; }

    public byte[] RowVersion { get; init; } = [];

    public bool IsNew { get; init; }

    public bool DobChanged { get; init; }

    public bool DeceasedChanged { get; init; }
}

/// <summary>Input for dbo.uspSavePerson.</summary>
public sealed record PersonSave(
    Guid PersonId,
    string LastName,
    string? FirstName,
    string? MiddleName,
    string LastNameKey,
    string? FirstNameKey,
    bool IsPlaceholderName,
    DateOnly? Dob,
    bool DobVerified,
    string Sex,
    string Ethnicity,
    string? PreferredLanguage,
    DateOnly? DeceasedDate,
    byte[]? RowVersion);

public sealed record RelationshipSave(
    Guid PersonRelationshipId,
    Guid PersonId,
    Guid RelatedPersonId,
    string Role,
    bool IsPrimaryContact,
    bool HasLegalCustody,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Source,
    DateOnly? VerifiedDate,
    bool IsDeleted,
    byte[]? RowVersion);

public sealed record PregnancySave(
    Guid PregnancyEpisodeId,
    Guid PersonId,
    DateOnly StartDate,
    DateOnly? ExpectedEndDate,
    DateOnly? ActualEndDate,
    string? Outcome,
    string Source,
    Guid? NewbornPersonId,
    bool IsDeleted,
    byte[]? RowVersion);

public sealed record AliasSave(Guid PersonId, string LastName, string? FirstName, string? MiddleName, string LastNameKey,
                               string? FirstNameKey, string AliasType, string Source, Guid? SourceLabResultId, DateOnly SeenDate);

// ------------------------------------------------------------------------------------------------ places

public sealed class LocationCandidateRow
{
    public Guid LocationId { get; init; }

    public string? AddressLine1 { get; init; }

    public string? Unit { get; init; }

    public string City { get; init; } = string.Empty;

    public string? State { get; init; }

    public string? Zip5 { get; init; }

    public string Country { get; init; } = "US";

    public string? CountyFips { get; init; }

    public string? CountyName { get; init; }

    public bool IsCityOnly { get; init; }

    public int? YearBuilt { get; init; }
}

public sealed record LocationSave(
    Guid LocationId,
    string Country,
    string? AddressLine1,
    string? Unit,
    string City,
    string? State,
    string? Zip5,
    string? Region,
    string? PostalCode,
    string? CountyFips,
    bool IsCityOnly,
    string AddressKey,
    string? BuildingAddressKey,
    int? YearBuilt,
    string? HousingType,
    string? Tenure);

public sealed class SaveLocationResult
{
    public Guid LocationId { get; init; }

    public bool WasExisting { get; init; }
}

public sealed class OrganizationSearchRow
{
    public Guid OrganizationId { get; init; }

    public string OrganizationName { get; init; } = string.Empty;

    public string? NaicsCode { get; init; }

    public string? CliaNumber { get; init; }

    public string? Npi { get; init; }

    public string? OrganizationTypes { get; init; }

    public Guid? OrganizationSiteId { get; init; }

    public Guid? SiteLocationId { get; init; }

    public string? SiteName { get; init; }

    public string? SiteAddress { get; init; }

    public string? SiteCity { get; init; }
}

public sealed class OrganizationRow
{
    public Guid OrganizationId { get; init; }

    public string OrganizationName { get; init; } = string.Empty;

    public string? NaicsCode { get; init; }

    public string? NaicsVersion { get; init; }

    public string? Npi { get; init; }

    public string? CliaNumber { get; init; }

    public Guid? ParentOrganizationId { get; init; }

    public string? ParentOrganizationName { get; init; }

    public string? ContactName { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public bool IsActive { get; init; }

    public byte[] RowVersion { get; init; } = [];
}

public sealed class OrganizationSiteRow
{
    public Guid OrganizationSiteId { get; init; }

    public Guid LocationId { get; init; }

    public string? SiteName { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public string? AddressLine1 { get; init; }

    public string? Unit { get; init; }

    public string City { get; init; } = string.Empty;

    public string? State { get; init; }

    public string? Zip5 { get; init; }

    public string? CountyFips { get; init; }
}

public sealed record OrganizationDetail(OrganizationRow Organization, IReadOnlyList<string> Types,
                                        IReadOnlyList<OrganizationSiteRow> Sites);

public sealed record OrganizationSave(
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationNameKey,
    string? NaicsCode,
    string? NaicsVersion,
    string? Npi,
    string? CliaNumber,
    Guid? ParentOrganizationId,
    string? ContactName,
    string? Phone,
    string? Email,
    bool IsActive,
    IReadOnlyList<string> Types,
    byte[]? RowVersion);

public sealed record EmploymentSave(
    Guid EmploymentId,
    Guid PersonId,
    Guid OrganizationId,
    Guid? WorksiteLocationId,
    string? JobTitle,
    string? IndustryText,
    string? OccupationCode,
    string? OccupationCodeVersion,
    string? NaicsOverride,
    string? MetalTasks,
    string? PpeNotes,
    decimal? HoursPerWeek,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Source,
    DateOnly? VerifiedDate,
    IReadOnlyList<Guid> AnalyteIds,
    bool IsDeleted,
    byte[]? RowVersion);

public sealed record ExposureSourceSave(
    Guid ExposureSourceId,
    Guid PersonId,
    Guid? ExposureCaseId,
    string SourceType,
    string? SourceDescription,
    Guid? ProductId,
    Guid? LocationId,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsConfirmed,
    bool IsDeleted);

public sealed class ProductRow
{
    public Guid ProductId { get; init; }

    public string? Brand { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public string? CountryOfOrigin { get; init; }

    public string? ProductType { get; init; }
}

public sealed class ProviderSearchRow
{
    public Guid ProviderId { get; init; }

    public string? Npi { get; init; }

    public string LastName { get; init; } = string.Empty;

    public string? FirstName { get; init; }

    public string? Credential { get; init; }

    public string RegistryStatus { get; init; } = string.Empty;

    public string? PracticeCity { get; init; }

    public string? PracticeState { get; init; }
}

public sealed class OrganizationResolveRow
{
    public Guid OrganizationId { get; init; }

    public string OrganizationName { get; init; } = string.Empty;

    public string? CliaNumber { get; init; }

    public string? Npi { get; init; }
}

/// <summary>One row of the reporter registry: a laboratory, a facility (ordering practice, clinic, hospital) or a provider.</summary>
public sealed class ReporterRow
{
    public string ReporterKind { get; init; } = string.Empty;

    public Guid ReporterId { get; init; }

    public string ReporterName { get; init; } = string.Empty;

    public string? CliaNumber { get; init; }

    public string? Npi { get; init; }

    public string? Credential { get; init; }

    public string? Phone { get; init; }

    public string? RegistryStatus { get; init; }

    public DateTime ModifiedDateUtc { get; init; }
}

/// <summary>A checked registry row ready for the database: laboratories and facilities carry a name, providers a person's name.</summary>
public sealed record ReporterSave(int RowNumber, string Kind, string? Name, string? NameKey, string? LastName, string? FirstName,
                                  string? Credential, string? Npi, string? Clia, string? Phone);

public sealed record RegistryImportCounts(int AddedCount, int UpdatedCount);
