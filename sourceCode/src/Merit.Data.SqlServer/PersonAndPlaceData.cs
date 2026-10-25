using System.Text.Json;
using Dapper;
using Merit.Application.Data;
using Merit.Application.Errors;

namespace Merit.Data.SqlServer;

public sealed class PersonData(Db db) : IPersonData
{
    public Task<SearchResults> SearchAsync(string searchText, string? nameKey1, string? nameKey2, DateOnly? dob, string? addressKey,
                                           int maxRows, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspSearch", new { searchText, nameKey1, nameKey2, dob, addressKey, maxRows }, async g =>
        {
            var persons = (await g.ReadAsync<SearchPersonRow>().ConfigureAwait(false)).AsList();
            var cases = (await g.ReadAsync<SearchCaseRow>().ConfigureAwait(false)).AsList();
            var orgs = (await g.ReadAsync<SearchOrganizationRow>().ConfigureAwait(false)).AsList();
            bool elsewhere = await g.ReadSingleAsync<bool>().ConfigureAwait(false);
            return new SearchResults(persons, cases, orgs, elsewhere);
        }, ct);

    public async Task<PersonHeader> GetPersonAsync(Guid personId, CancellationToken ct = default)
        => await db.MultipleAsync("dbo.uspGetPerson", new { personId }, async g =>
        {
            var person = await g.ReadFirstOrDefaultAsync<PersonRow>().ConfigureAwait(false) ?? throw new NotFoundException();
            return new PersonHeader(person,
                (await g.ReadAsync<AliasRow>().ConfigureAwait(false)).AsList(),
                (await g.ReadAsync<RaceRow>().ConfigureAwait(false)).AsList(),
                (await g.ReadAsync<ContactMethodRow>().ConfigureAwait(false)).AsList(),
                (await g.ReadAsync<IdentifierRow>().ConfigureAwait(false)).AsList(),
                (await g.ReadAsync<OpenCaseChipRow>().ConfigureAwait(false)).AsList());
        }, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<RelationshipRow>> GetRelationshipsAsync(Guid personId, CancellationToken ct = default)
        => db.QueryAsync<RelationshipRow>("dbo.uspGetPersonRelationships", new { personId }, ct);

    public Task<IReadOnlyList<PlaceListRow>> GetPlacesAsync(Guid personId, CancellationToken ct = default)
        => db.QueryAsync<PlaceListRow>("dbo.uspGetPersonPlaces", new { personId }, ct);

    public Task<IReadOnlyList<EmploymentRow>> GetEmploymentAsync(Guid personId, CancellationToken ct = default)
        => db.QueryAsync<EmploymentRow>("dbo.uspGetPersonEmployment", new { personId }, ct);

    public Task<IReadOnlyList<ExposureSourceRow>> GetExposureSourcesAsync(Guid personId, CancellationToken ct = default)
        => db.QueryAsync<ExposureSourceRow>("dbo.uspGetPersonExposureSources", new { personId }, ct);

    public Task<IReadOnlyList<PregnancyRow>> GetPregnanciesAsync(Guid personId, CancellationToken ct = default)
        => db.QueryAsync<PregnancyRow>("dbo.uspGetPersonPregnancies", new { personId }, ct);

    public Task<IReadOnlyList<PersonCaseRow>> GetCasesAsync(Guid personId, CancellationToken ct = default)
        => db.QueryAsync<PersonCaseRow>("dbo.uspGetPersonCases", new { personId }, ct);

    public Task<IReadOnlyList<PersonResultRow>> GetResultsAsync(Guid personId, Guid? analyteId, CancellationToken ct = default)
        => db.QueryAsync<PersonResultRow>("dbo.uspGetPersonResults", new { personId, analyteId }, ct);

    public async Task<SavePersonResult> SavePersonAsync(PersonSave p, CancellationToken ct = default)
        => await db.QuerySingleAsync<SavePersonResult>("dbo.uspSavePerson", new
        {
            p.PersonId, p.LastName, p.FirstName, p.MiddleName, p.LastNameKey, p.FirstNameKey, p.IsPlaceholderName, p.Dob,
            p.DobVerified, p.Sex, p.Ethnicity, p.PreferredLanguage, p.DeceasedDate, p.RowVersion,
        }, ct).ConfigureAwait(false) ?? throw new NotFoundException();

    public Task SaveRacesAsync(Guid personId, IReadOnlyList<string> raceCodes, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSavePersonRaces", new { personId, raceCodesJson = JsonSerializer.Serialize(raceCodes) }, ct);

    public Task SaveContactMethodAsync(Guid id, Guid personId, string contactType, string contactValue, bool isPreferred, bool isDeleted,
                                       CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSavePersonContactMethod",
            new { personContactMethodId = id, personId, contactType, contactValue, isPreferred, isDeleted }, ct);

    public Task SaveIdentifierAsync(Guid id, Guid personId, string identifierType, string identifierValue, string? assigningFacility,
                                    string source, DateOnly reportedDate, bool isDeleted, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSavePersonIdentifier", new
        {
            personIdentifierId = id, personId, identifierType, identifierValue, assigningFacility, source, reportedDate, isDeleted,
        }, ct);

    public Task SaveAliasAsync(AliasSave a, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSavePersonAlias", new
        {
            a.PersonId, a.LastName, a.FirstName, a.MiddleName, a.LastNameKey, a.FirstNameKey, a.AliasType, a.Source,
            a.SourceLabResultId, a.SeenDate,
        }, ct);

    public Task SavePregnancyAsync(PregnancySave p, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSavePregnancyEpisode", new
        {
            p.PregnancyEpisodeId, p.PersonId, p.StartDate, p.ExpectedEndDate, p.ActualEndDate, p.Outcome, p.Source,
            p.NewbornPersonId, p.IsDeleted, p.RowVersion,
        }, ct);

    public Task SaveRelationshipAsync(RelationshipSave r, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSavePersonRelationship", new
        {
            r.PersonRelationshipId, r.PersonId, r.RelatedPersonId, r.Role, r.IsPrimaryContact, r.HasLegalCustody, r.StartDate,
            r.EndDate, r.Source, r.VerifiedDate, r.IsDeleted, r.RowVersion,
        }, ct);

    public Task<IReadOnlyList<PersonCandidateRow>> FindCandidatesAsync(string lastNameKey, string? firstNameKey, DateOnly? dob, int maxRows,
                                                                      string? incomingKey = null, CancellationToken ct = default)
        => db.QueryAsync<PersonCandidateRow>("dbo.uspFindPersonCandidates", new { lastNameKey, firstNameKey, dob, maxRows, incomingKey }, ct);
}

public sealed class PlaceData(Db db) : IPlaceData
{
    public Task<IReadOnlyList<LocationCandidateRow>> FindLocationsAsync(string addressKeyPrefix, string? zip5, int maxRows, CancellationToken ct = default)
        => db.QueryAsync<LocationCandidateRow>("dbo.uspFindLocations", new { addressKeyPrefix, zip5, maxRows }, ct);

    public async Task<SaveLocationResult> SaveLocationAsync(LocationSave l, CancellationToken ct = default)
        => await db.QuerySingleAsync<SaveLocationResult>("dbo.uspSaveLocation", new
        {
            l.LocationId, l.Country, l.AddressLine1, l.Unit, l.City, l.State, l.Zip5, l.Region, l.PostalCode, l.CountyFips,
            l.IsCityOnly, l.AddressKey, l.BuildingAddressKey, l.YearBuilt, l.HousingType, l.Tenure,
        }, ct).ConfigureAwait(false) ?? throw new NotFoundException();

    public Task SavePersonLocationsAsync(Guid personId, string changesJson, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSavePersonLocations", new { personId, changesJson }, ct);

    public Task<IReadOnlyList<OrganizationSearchRow>> SearchOrganizationsAsync(string nameKey, string? organizationTypeCode, int maxRows,
                                                                              CancellationToken ct = default)
        => db.QueryAsync<OrganizationSearchRow>("dbo.uspSearchOrganizations", new { nameKey, organizationTypeCode, maxRows }, ct);

    public Task<OrganizationDetail?> GetOrganizationAsync(Guid organizationId, CancellationToken ct = default)
        => db.MultipleAsync("dbo.uspGetOrganization", new { organizationId }, async g =>
        {
            var org = await g.ReadFirstOrDefaultAsync<OrganizationRow>().ConfigureAwait(false);
            var types = (await g.ReadAsync<string>().ConfigureAwait(false)).AsList();
            var sites = (await g.ReadAsync<OrganizationSiteRow>().ConfigureAwait(false)).AsList();
            return org is null ? null : new OrganizationDetail(org, types, sites);
        }, ct);

    public Task SaveOrganizationAsync(OrganizationSave o, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveOrganization", new
        {
            o.OrganizationId, o.OrganizationName, o.OrganizationNameKey, o.NaicsCode, o.NaicsVersion, o.Npi, o.CliaNumber,
            o.ParentOrganizationId, o.ContactName, o.Phone, o.Email, o.IsActive, typesJson = JsonSerializer.Serialize(o.Types),
            o.RowVersion,
        }, ct);

    public Task SaveOrganizationSiteAsync(Guid organizationSiteId, Guid organizationId, Guid locationId, string? siteName, DateOnly? startDate,
                                          DateOnly? endDate, bool isDeleted, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveOrganizationSite",
            new { organizationSiteId, organizationId, locationId, siteName, startDate, endDate, isDeleted }, ct);

    public Task SaveEmploymentAsync(EmploymentSave e, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveEmployment", new
        {
            e.EmploymentId, e.PersonId, e.OrganizationId, e.WorksiteLocationId, e.JobTitle, e.IndustryText, e.OccupationCode,
            e.OccupationCodeVersion, e.NaicsOverride, e.MetalTasks, e.PpeNotes, e.HoursPerWeek, e.StartDate, e.EndDate, e.Source,
            e.VerifiedDate, analyteIdsJson = JsonSerializer.Serialize(e.AnalyteIds), e.IsDeleted, e.RowVersion,
        }, ct);

    public Task SaveExposureSourceAsync(ExposureSourceSave s, CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveExposureSource", new
        {
            s.ExposureSourceId, s.PersonId, s.ExposureCaseId, s.SourceType, s.SourceDescription, s.ProductId, s.LocationId,
            s.StartDate, s.EndDate, s.IsConfirmed, s.IsDeleted,
        }, ct);

    public Task<IReadOnlyList<ProductRow>> SearchProductsAsync(string text, int maxRows, CancellationToken ct = default)
        => db.QueryAsync<ProductRow>("dbo.uspSearchProducts", new { text, maxRows }, ct);

    public Task SaveProductAsync(Guid productId, string? brand, string productName, string? countryOfOrigin, string? productType, string? notes,
                                 CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveProduct", new { productId, brand, productName, countryOfOrigin, productType, notes }, ct);

    public Task<IReadOnlyList<ProviderSearchRow>> SearchProvidersAsync(string text, int maxRows, CancellationToken ct = default)
        => db.QueryAsync<ProviderSearchRow>("dbo.uspSearchProviders", new { text, maxRows }, ct);

    public Task SaveProviderAsync(Guid providerId, string? npi, string lastName, string? firstName, string? credential, Guid? practiceLocationId,
                                  CancellationToken ct = default)
        => db.ExecuteAsync("dbo.uspSaveProvider", new { providerId, npi, lastName, firstName, credential, practiceLocationId }, ct);

    public Task<OrganizationResolveRow?> ResolveOrganizationAsync(string? cliaNumber, string? npi, string? nameKey, CancellationToken ct = default)
        => db.QuerySingleAsync<OrganizationResolveRow>("dbo.uspResolveOrganization", new { cliaNumber, npi, nameKey }, ct);

    public Task<IReadOnlyList<ReporterRow>> ListReportersAsync(string? text, string? kind, int maxRows, CancellationToken ct = default)
        => db.QueryAsync<ReporterRow>("dbo.uspListReporters", new { text, kind, maxRows }, ct);

    public async Task<RegistryImportCounts> ImportReportersAsync(IReadOnlyList<ReporterSave> rows, CancellationToken ct = default)
        => await db.QuerySingleAsync<RegistryImportCounts>("dbo.uspImportReporters", new
        {
            rowsJson = System.Text.Json.JsonSerializer.Serialize(rows.Select(r => new
            {
                rowNumber = r.RowNumber, kind = r.Kind, name = r.Name, nameKey = r.NameKey, lastName = r.LastName, firstName = r.FirstName,
                credential = r.Credential, npi = r.Npi, clia = r.Clia, phone = r.Phone,
            })),
        }, ct).ConfigureAwait(false) ?? new RegistryImportCounts(0, 0);
}
