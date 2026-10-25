using System.ComponentModel.DataAnnotations;
using Merit.Application.Data;
using Merit.Application.Services;

namespace Merit.Web.Models;

// View models: each Razor view receives one built for it. They hold application read models (rows returned by the data
// interface) and form fields, never domain types (architecture rule 2, checked by the architecture tests).

public sealed record AlertModel(string Kind, string Title, string Message);

public sealed record SelectOption(string Value, string Label, bool Selected = false);

// ------------------------------------------------------------------------------------------------ account

public sealed class SignInModel
{
    [Required(ErrorMessage = "Enter your user name.")]
    public string? UserName { get; set; }

    [Required(ErrorMessage = "Enter your password.")]
    [DataType(DataType.Password)]
    public string? Password { get; set; }

    public string? ReturnUrl { get; set; }

    public string? Message { get; set; }
}

public sealed class MfaModel
{
    [Required(ErrorMessage = "Enter the six-digit code from your authenticator app, or a recovery code.")]
    public string? Code { get; set; }

    public string? Message { get; set; }
}

public sealed class EnrollModel
{
    public string SecretBase32 { get; set; } = string.Empty;

    public string ProvisioningUri { get; set; } = string.Empty;

    public string QrSvg { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the six-digit code your authenticator app shows.")]
    public string? Code { get; set; }

    public bool IsAdministrator { get; set; }

    public string? Message { get; set; }
}

public sealed record RecoveryCodesModel(IReadOnlyList<string> Codes);

// ------------------------------------------------------------------------------------------------ home and search

public sealed record QueueTilesModel(IReadOnlyList<QueueCountRow> Queues);

public sealed record DashboardModel(DashboardData Data, QueueTilesModel Tiles, string DisplayName, string? TenantName);

public sealed record SearchModel(string? Query, SearchResults? Results);

// ------------------------------------------------------------------------------------------------ person page

/// <summary>One section of the person page; the population picks the order (specification, Person page table).</summary>
public sealed record PersonSection(string Key, string Title, int Order);

public sealed record PersonPageModel(
    PersonPage Page,
    IReadOnlyList<PersonSection> Sections,
    RelationshipsModel Relationships,
    PlacesModel Places,
    ResultsSectionModel Results,
    CasesSectionModel Cases,
    EmploymentModel Employment,
    SourcesModel Sources,
    PregnancyModel Pregnancy,
    bool CanEdit,
    bool CanEnterResults,
    bool CanViewIdentifiers)
{
    public PersonRow Person => Page.Header.Person;

    public string FullName => string.Join(' ', new[] { Person.FirstName, Person.MiddleName, Person.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed record PersonHeaderModel(PersonHeader Header, string Population, int? AgeYears, bool CanEdit, bool CanViewIdentifiers);

public sealed record RelationshipsModel(Guid PersonId, IReadOnlyList<RelationshipRow> Rows, bool CanEdit, string EmptyText);

public sealed record PlacesModel(Guid PersonId, IReadOnlyList<PlaceListRow> Rows, bool CanEdit, IReadOnlyDictionary<string, string> RoleLabels,
                                 IReadOnlyDictionary<string, string> ReasonLabels);

public sealed record ChartPoint(DateOnly Date, decimal Value, string? SampleType, string Operator);

public sealed record TrendChart(string AnalyteCode, string AnalyteName, string Unit, IReadOnlyList<ChartPoint> Points,
                                IReadOnlyList<(string Label, decimal Value)> Levels);

public sealed record ResultsSectionModel(Guid PersonId, IReadOnlyList<PersonResultRow> Rows, Guid? AnalyteFilter,
                                         IReadOnlyList<SelectOption> AnalyteOptions, IReadOnlyList<TrendChart> Charts, bool CanEnter);

public sealed record CasesSectionModel(Guid PersonId, IReadOnlyList<PersonCaseRow> Rows);

public sealed record EmploymentModel(Guid PersonId, IReadOnlyList<EmploymentRow> Rows, bool CanEdit);

public sealed record SourcesModel(Guid PersonId, IReadOnlyList<ExposureSourceRow> Rows, bool CanEdit, IReadOnlyList<SelectOption> SourceTypes);

public sealed record PregnancyModel(Guid PersonId, IReadOnlyList<PregnancyRow> Rows, bool CanEdit, bool Show, bool IsMale);

public sealed class PersonFormModel
{
    public Guid? PersonId { get; set; }

    [Required(ErrorMessage = "Last name is required.")]
    [StringLength(60)]
    public string? LastName { get; set; }

    [StringLength(60)]
    public string? FirstName { get; set; }

    [StringLength(60)]
    public string? MiddleName { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? Dob { get; set; }

    public bool DobVerified { get; set; }

    public string Sex { get; set; } = "unknown";

    public string Ethnicity { get; set; } = "unknown";

    public string? PreferredLanguage { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? DeceasedDate { get; set; }

    public List<string> RaceCodes { get; set; } = [];

    public string? RowVersion { get; set; }

    public IReadOnlyList<SelectOption> Races { get; set; } = [];

    public IReadOnlyList<SelectOption> Languages { get; set; } = [];

    public IReadOnlyList<string> Notes { get; set; } = [];
}

public sealed class RelationshipFormModel
{
    public Guid PersonId { get; set; }

    public Guid? RelatedPersonId { get; set; }

    public string? RelatedPersonLabel { get; set; }

    public string? NewLastName { get; set; }

    public string? NewFirstName { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? NewDob { get; set; }

    public string Role { get; set; } = "parent";

    public bool IsPrimaryContact { get; set; }

    public bool HasLegalCustody { get; set; }

    public string Source { get; set; } = "interview";

    public IReadOnlyList<SelectOption> Roles { get; set; } = [];
}

public sealed class ContactFormModel
{
    public string ContactType { get; set; } = "phone";

    public string? ContactValue { get; set; }

    public bool IsPreferred { get; set; }
}

public sealed class PlaceFormModel
{
    public Guid PersonId { get; set; }

    public string Role { get; set; } = "residence";

    public Guid? LocationId { get; set; }

    public string? LocationLabel { get; set; }

    public Guid? OrganizationSiteId { get; set; }

    /// <summary>The site picker's value, "siteId:locationId".</summary>
    public string? OrganizationSite { get; set; }

    public string? OrganizationLabel { get; set; }

    public string? AddressLine1 { get; set; }

    public string? Unit { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Zip5 { get; set; }

    public string? CountyFips { get; set; }

    public bool IsCityOnly { get; set; }

    public int? YearBuilt { get; set; }

    public string? ScheduleType { get; set; }

    public decimal? HoursPerWeek { get; set; }

    public string? ScheduleNote { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? StartDate { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? EndDate { get; set; }

    public string Source { get; set; } = "interview";

    /// <summary>"new" or "existing" when the residence overlaps another; required then (address rule 2).</summary>
    public string? PrimaryChoice { get; set; }

    public string? PrimaryReason { get; set; }

    public bool NeedsPrimaryChoice { get; set; }

    public string? OverlapMessage { get; set; }

    public IReadOnlyList<SelectOption> Roles { get; set; } = [];

    public IReadOnlyList<SelectOption> Schedules { get; set; } = [];

    public IReadOnlyList<SelectOption> Reasons { get; set; } = [];

    public IReadOnlyList<SelectOption> Counties { get; set; } = [];
}

public sealed class SetPrimaryModel
{
    public Guid PersonId { get; set; }

    public Guid PersonLocationId { get; set; }

    public string AddressLabel { get; set; } = string.Empty;

    [DataType(DataType.Date)]
    public DateOnly? StartDate { get; set; }

    public string? PrimaryReason { get; set; }

    public IReadOnlyList<SelectOption> Reasons { get; set; } = [];

    public IReadOnlyList<string> Changes { get; set; } = [];

    public IReadOnlyList<string> Errors { get; set; } = [];

    public IReadOnlyList<string> Warnings { get; set; } = [];

    public string? CountyBefore { get; set; }

    public string? CountyAfter { get; set; }

    public bool Previewed { get; set; }
}

public sealed class EmploymentFormModel
{
    public Guid PersonId { get; set; }

    public Guid? OrganizationId { get; set; }

    public string? OrganizationLabel { get; set; }

    public string? NewOrganizationName { get; set; }

    public string? WorksiteAddressLine1 { get; set; }

    public string? WorksiteCity { get; set; }

    public string? WorksiteState { get; set; }

    public string? WorksiteZip5 { get; set; }

    public string? WorksiteCountyFips { get; set; }

    public string? JobTitle { get; set; }

    public string? IndustryText { get; set; }

    public string? OccupationCode { get; set; }

    public string? NaicsOverride { get; set; }

    public string? MetalTasks { get; set; }

    public string? PpeNotes { get; set; }

    public decimal? HoursPerWeek { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? StartDate { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? EndDate { get; set; }

    public string Source { get; set; } = "interview";

    public List<Guid> AnalyteIds { get; set; } = [];

    public IReadOnlyList<SelectOption> Analytes { get; set; } = [];
}

public sealed class SourceFormModel
{
    public Guid PersonId { get; set; }

    public string SourceType { get; set; } = "consumerProduct";

    public string? Description { get; set; }

    public string? ProductName { get; set; }

    public string? ProductBrand { get; set; }

    public string? ProductCountry { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? StartDate { get; set; }

    public bool IsConfirmed { get; set; }

    public IReadOnlyList<SelectOption> SourceTypes { get; set; } = [];
}

public sealed class PregnancyFormModel
{
    public Guid PersonId { get; set; }

    public Guid? PregnancyEpisodeId { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? StartDate { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? ExpectedEndDate { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? ActualEndDate { get; set; }

    public string? Outcome { get; set; }

    public string Source { get; set; } = "provider";

    public string? RowVersion { get; set; }
}

// ------------------------------------------------------------------------------------------------ results

public sealed class ResultFormModel
{
    public Guid PersonId { get; set; }

    public string PersonName { get; set; } = string.Empty;

    public Guid? AnalyteId { get; set; }

    public Guid? AnalyteSpecimenId { get; set; }

    public string? ValueText { get; set; }

    public string? Unit { get; set; }

    public string? SampleType { get; set; }

    public string? Speciation { get; set; }

    public decimal? Creatinine { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? CollectedDate { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? AnalyzedDate { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? ReportedDate { get; set; }

    public string? TestMethod { get; set; }

    public Guid? LabOrganizationId { get; set; }

    public string? LabLabel { get; set; }

    public Guid? OrderingProviderId { get; set; }

    public string? ProviderLabel { get; set; }

    public string? AccessionNumber { get; set; }

    public string ResultStatus { get; set; } = "final";

    public string? ReasonForTest { get; set; }

    public string? PayerType { get; set; }

    public ResultFieldsModel Fields { get; set; } = new();

    public IReadOnlyList<SelectOption> Analytes { get; set; } = [];

    public IReadOnlyList<SelectOption> Methods { get; set; } = [];

    public IReadOnlyList<SelectOption> Reasons { get; set; } = [];

    public IReadOnlyList<SelectOption> Payers { get; set; } = [];
}

/// <summary>The fields that cascade from the analyte lookup, re-rendered as one partial per change.</summary>
public sealed class ResultFieldsModel
{
    public IReadOnlyList<SelectOption> Specimens { get; set; } = [];

    public IReadOnlyList<SelectOption> Units { get; set; } = [];

    public bool UsesSampleType { get; set; }

    public IReadOnlyList<SelectOption> SampleTypes { get; set; } = [];

    public IReadOnlyList<SelectOption> Speciations { get; set; } = [];

    public bool RequiresCreatinine { get; set; }

    public string? CanonicalUnit { get; set; }
}

public sealed record ResultSavedModel(Guid PersonId, ResultSaveSummary Summary);

public sealed record ResultDetailModel(LabResultDetailRow Result, IReadOnlyList<KeyValuePair<string, string?>> Snapshot);

// ------------------------------------------------------------------------------------------------ cases and investigations

public sealed record CasePageModel(CaseDetail Detail, IReadOnlyList<SelectOption> Users, IReadOnlyList<SelectOption> ClosureReasons,
                                   IReadOnlyList<SelectOption> ActivityTypes, IReadOnlyList<SelectOption> Outcomes, bool CanEdit,
                                   bool CanInvestigate, string? Message = null);

public sealed record CaseListModel(IReadOnlyList<CaseListRow> Rows, bool OpenOnly, bool AssignedToMe, int PageNumber, int TotalCount);

public sealed class InvestigationRequestModel
{
    public Guid ExposureCaseId { get; set; }

    public string CaseNumber { get; set; } = string.Empty;

    public string InvestigationType { get; set; } = "residential";

    public string? Target { get; set; }

    public IReadOnlyList<SelectOption> Targets { get; set; } = [];

    public IReadOnlyList<OpenInvestigationRow> Existing { get; set; } = [];

    public Guid? JoinInvestigationId { get; set; }

    public bool Checked { get; set; }
}

public sealed record InvestigationPageModel(InvestigationDetail Detail, IReadOnlyList<SelectOption> Users,
                                            IReadOnlyList<SelectOption> Statuses, IReadOnlyList<SelectOption> RegulatoryActions,
                                            IReadOnlyList<SelectOption> SampleTypes, bool CanEdit);

public sealed record InvestigationListModel(IReadOnlyList<InvestigationListRow> Rows, bool OpenOnly, int PageNumber);

// ------------------------------------------------------------------------------------------------ configuration and admin

public sealed record ThresholdListModel(IReadOnlyList<ThresholdRow> Rows, bool CanEdit);

public sealed class ThresholdFormModel
{
    public Guid? ThresholdId { get; set; }

    public Guid? AnalyteSpecimenId { get; set; }

    public string Population { get; set; } = "child";

    public int? AgeMinMonths { get; set; }

    public int? AgeMaxMonths { get; set; }

    public string? Speciation { get; set; }

    public string? SampleType { get; set; }

    public string ThresholdType { get; set; } = "case";

    public Guid? ActionCodeId { get; set; }

    public int? DueIntervalDays { get; set; }

    public decimal? LowerValue { get; set; }

    public decimal? UpperValue { get; set; }

    public string? Unit { get; set; }

    public string Comparator { get; set; } = ">=";

    [DataType(DataType.Date)]
    public DateOnly? EffectiveFrom { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? EffectiveTo { get; set; }

    public string? Authority { get; set; }

    public bool IsActive { get; set; } = true;

    public string? RowVersion { get; set; }

    public IReadOnlyList<SelectOption> Specimens { get; set; } = [];

    public IReadOnlyList<SelectOption> ActionCodes { get; set; } = [];
}

public sealed record SettingsModel(IReadOnlyList<SettingRow> Current, IReadOnlyList<SettingRow> History, bool CanEdit);

public sealed record AnalytesModel(IReadOnlyList<AnalyteRow> Analytes, IReadOnlyList<AnalyteSpecimenRow> Specimens, bool CanEdit);

public sealed record UsersModel(IReadOnlyList<UserListRow> Users);

public sealed class UserFormModel
{
    public Guid? AppUserId { get; set; }

    [Required(ErrorMessage = "User name is required.")]
    public string? UserName { get; set; }

    [Required(ErrorMessage = "Display name is required.")]
    public string? DisplayName { get; set; }

    [EmailAddress]
    public string? Email { get; set; }

    public bool IsActive { get; set; } = true;

    [DataType(DataType.Password)]
    public string? NewPassword { get; set; }

    public bool MfaEnabled { get; set; }

    /// <summary>Tenant the person works for; its administrators manage the account.</summary>
    public Guid? HomeTenantId { get; set; }

    public string? HomeTenantName { get; set; }

    /// <summary>False when the caller can grant access here but the account belongs to another tenant's administrators.</summary>
    public bool CanManageAccount { get; set; } = true;

    public bool CanViewAs { get; set; }

    public IReadOnlyList<UserAssignmentRow> Assignments { get; set; } = [];

    public IReadOnlyList<SelectOption> Roles { get; set; } = [];

    public IReadOnlyList<SelectOption> Tenants { get; set; } = [];
}

public sealed record IntakeModel(IReadOnlyList<IntakeProfileListRow> Profiles, IReadOnlyList<IntakeBatchRow> Batches, string? Message);

public sealed record ExtractsModel(IReadOnlyList<ExtractRunRow> Runs, bool AnyRunning, int Year, int Quarter);

public sealed record AuditModel(IReadOnlyList<AuditEventRow> Events, Guid? EntityId);

public sealed record LookupOptionsModel(string FieldId, IReadOnlyList<(Guid Id, string Label, string? Detail)> Options, string? Extra = null);

public sealed record AngularHostModel(IReadOnlyList<string> Scripts, IReadOnlyList<string> Styles, bool IsBuilt);

/// <summary>The tenant tree and the Add tenant form; the type, code and county fields depend on the chosen parent.</summary>
public sealed class TenantFormModel
{
    public Guid? ParentTenantId { get; set; }

    public string? TenantCode { get; set; }

    public string? TenantName { get; set; }

    public string? TenantType { get; set; }

    public string? StatePostalCode { get; set; }

    public string? StateFips { get; set; }

    public string? CountyFipsList { get; set; }

    public string? NewCounties { get; set; }

    public string? ParentType { get; set; }

    public string? ParentName { get; set; }

    public IReadOnlyList<string> TypeOptions { get; set; } = [];

    public IReadOnlyList<SelectOption> Parents { get; set; } = [];

    public IReadOnlyList<TenantListRow> Tree { get; set; } = [];

    public IReadOnlyList<SubJurisdictionRow> ExistingCounties { get; set; } = [];
}

public sealed class ReporterFormModel
{
    public string? Kind { get; set; } = "laboratory";

    public string? Name { get; set; }

    public string? LastName { get; set; }

    public string? FirstName { get; set; }

    public string? Credential { get; set; }

    public string? Npi { get; set; }

    public string? Clia { get; set; }

    public string? Phone { get; set; }
}

public sealed record ReportersModel(IReadOnlyList<ReporterRow> Rows, string? Query, string? Kind, string? Message, bool CanEdit, ReporterFormModel Form);

public sealed class RegistryImportModel
{
    public string? FileName { get; init; }

    public string? Error { get; init; }

    public string? Token { get; init; }

    public RegistryCheck? Check { get; init; }

    public RegistryImportCounts? Counts { get; init; }
}

public sealed record ExtractFindingsModel(ExtractRunRow Run, IReadOnlyList<Merit.Application.Extracts.ExtractFinding> Findings);
