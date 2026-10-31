using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Merit.Domain.Identifiers;
using Merit.Domain.Population;
using Merit.Domain.Text;

namespace Merit.Application.Services;

public sealed record PersonInput(
    Guid? PersonId,
    string? LastName,
    string? FirstName,
    string? MiddleName,
    DateOnly? Dob,
    bool DobVerified,
    string Sex,
    string Ethnicity,
    string? PreferredLanguage,
    DateOnly? DeceasedDate,
    IReadOnlyList<string> RaceCodes,
    byte[]? RowVersion);

public sealed record PersonSaveOutcome(Guid PersonId, bool IsNew, IReadOnlyList<string> Changes);

/// <summary>Everything on the person page, in one read model; sections are ordered by population in the web layer.</summary>
public sealed record PersonPage(
    PersonHeader Header,
    string CurrentPopulation,
    int? AgeYears,
    IReadOnlyList<RelationshipRow> Relationships,
    IReadOnlyList<PlaceListRow> Places,
    IReadOnlyList<PersonResultRow> Results,
    IReadOnlyList<PersonCaseRow> Cases,
    IReadOnlyList<EmploymentRow> Employment,
    IReadOnlyList<ExposureSourceRow> ExposureSources,
    IReadOnlyList<PregnancyRow> Pregnancies);

public sealed record RelationshipInput(
    Guid PersonId,
    Guid? RelatedPersonId,
    string? NewLastName,
    string? NewFirstName,
    DateOnly? NewDob,
    string? NewSex,
    string Role,
    bool IsPrimaryContact,
    bool HasLegalCustody,
    DateOnly? StartDate,
    string Source);

public sealed record EmploymentInput(
    Guid? EmploymentId,
    Guid PersonId,
    Guid? OrganizationId,
    string? NewOrganizationName,
    AddressInput? WorksiteAddress,
    Guid? WorksiteLocationId,
    string? JobTitle,
    string? IndustryText,
    string? OccupationCode,
    string? NaicsOverride,
    string? MetalTasks,
    string? PpeNotes,
    decimal? HoursPerWeek,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Source,
    IReadOnlyList<Guid> AnalyteIds, byte[]? RowVersion = null, bool Verify = false);

public sealed record ExposureSourceInput(Guid PersonId, string SourceType, string? Description, string? ProductName,
                                         string? ProductBrand, string? ProductCountry, DateOnly? StartDate, DateOnly? EndDate,
                                         bool IsConfirmed, Guid? ExposureSourceId = null, DateTime? OriginalModifiedUtc = null);

public sealed record PregnancyInput(Guid? PregnancyEpisodeId, Guid PersonId, DateOnly StartDate, DateOnly? ExpectedEndDate,
                                    DateOnly? ActualEndDate, string? Outcome, string Source, byte[]? RowVersion);

/// <summary>
/// The person registry: identity only, with relationships, jobs, sources and pregnancies as linked records with their own
/// dates. Every view and change is audited. A DOB or pregnancy change reruns classification and reports what changed.
/// </summary>
public sealed class PersonService(
    IClock clock,
    IPersonData data,
    IPlaceData places,
    IWorkData work,
    ICaseData cases,
    ClassificationService classification,
    PlaceService placeService,
    AuditService audit,
    Reference.ISettingsProvider settings,
    ICurrentSession session)
{
    private static readonly string[] Sexes = ["female", "male", "other", "unknown"];
    private static readonly string[] Ethnicities = ["hispanic", "notHispanic", "unknown"];
    private static readonly string[] OrgSuffixes = ["INC", "LLC", "CORP", "CORPORATION", "COMPANY", "CO", "LTD", "SERVICES", "LP", "LLP", "PLLC", "GROUP", "ENTERPRISES"];

    public async Task<PersonPage> GetPageAsync(Guid personId, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        var header = await data.GetPersonAsync(personId, ct).ConfigureAwait(false);
        var relationships = data.GetRelationshipsAsync(personId, ct);
        var placeRows = data.GetPlacesAsync(personId, ct);
        var results = data.GetResultsAsync(personId, null, ct);
        var caseRows = data.GetCasesAsync(personId, ct);
        var jobs = data.GetEmploymentAsync(personId, ct);
        var sources = data.GetExposureSourcesAsync(personId, ct);
        var pregnancies = data.GetPregnanciesAsync(personId, ct);
        await Task.WhenAll(relationships, placeRows, results, caseRows, jobs, sources, pregnancies).ConfigureAwait(false);
        await audit.ViewedAsync("person", personId, ct).ConfigureAwait(false);

        var config = await settings.GetAsync(ct).ConfigureAwait(false);
        var today = clock.Today;
        var episodes = pregnancies.Result.Select(e => new PregnancyEpisode(e.StartDate, e.ExpectedEndDate, e.ActualEndDate));
        var population = PopulationRules.Derive(header.Person.Dob, today, episodes, config.AdultAgeCutoffYears, config.PregnancyMaxWeeks);
        int? ageYears = header.Person.Dob is DateOnly dob ? PopulationRules.AgeInYears(dob, today) : null;

        return new PersonPage(header, population.Population.ToCode(), ageYears, relationships.Result, placeRows.Result, results.Result,
            caseRows.Result, jobs.Result, sources.Result, pregnancies.Result);
    }

    public async Task<PersonHeader> GetHeaderAsync(Guid personId, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonView);
        return await data.GetPersonAsync(personId, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RelationshipRow>> GetRelationshipsAsync(Guid personId, CancellationToken ct = default)
        => Read(() => data.GetRelationshipsAsync(personId, ct));

    public Task<IReadOnlyList<EmploymentRow>> GetEmploymentAsync(Guid personId, CancellationToken ct = default)
        => Read(() => data.GetEmploymentAsync(personId, ct));

    public Task<IReadOnlyList<ExposureSourceRow>> GetExposureSourcesAsync(Guid personId, CancellationToken ct = default)
        => Read(() => data.GetExposureSourcesAsync(personId, ct));

    public Task<IReadOnlyList<PregnancyRow>> GetPregnanciesAsync(Guid personId, CancellationToken ct = default)
        => Read(() => data.GetPregnanciesAsync(personId, ct));

    public Task<IReadOnlyList<PersonResultRow>> GetResultsAsync(Guid personId, Guid? analyteId, CancellationToken ct = default)
        => Read(() => data.GetResultsAsync(personId, analyteId, ct));

    public Task<IReadOnlyList<PersonCaseRow>> GetCasesAsync(Guid personId, CancellationToken ct = default)
        => Read(() => data.GetCasesAsync(personId, ct));

    public async Task<PersonSaveOutcome> SaveAsync(PersonInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var v = new Validator();
        v.Required(nameof(input.LastName), input.LastName, "Last name").MaxLength(nameof(input.LastName), input.LastName, 60, "Last name");
        v.MaxLength(nameof(input.FirstName), input.FirstName, 60, "First name").MaxLength(nameof(input.MiddleName), input.MiddleName, 60, "Middle name");
        var today = clock.Today;
        v.When(input.Dob > today, nameof(input.Dob), "Date of birth cannot be in the future.");
        v.When(input.Dob < new DateOnly(1900, 1, 1), nameof(input.Dob), "Date of birth cannot be before 1900.");
        v.When(input.DeceasedDate is not null && input.Dob is not null && input.DeceasedDate < input.Dob, nameof(input.DeceasedDate),
            "Date of death must be on or after the date of birth.");
        v.When(input.DeceasedDate > today, nameof(input.DeceasedDate), "Date of death cannot be in the future.");
        v.When(!Sexes.Contains(input.Sex), nameof(input.Sex), "Choose a sex.");
        v.When(!Ethnicities.Contains(input.Ethnicity), nameof(input.Ethnicity), "Choose an ethnicity.");
        v.ThrowIfInvalid();

        Guid id = input.PersonId ?? TimeOrderedId.New();
        string lastName = input.LastName!.Trim();
        string? firstName = string.IsNullOrWhiteSpace(input.FirstName) ? null : input.FirstName.Trim();
        var saved = await data.SavePersonAsync(new PersonSave(id, lastName, firstName, Clean(input.MiddleName),
            MatchKeys.NameKey(lastName) ?? lastName.ToUpperInvariant(), MatchKeys.NameKey(firstName),
            MatchKeys.IsPlaceholderName(firstName), input.Dob, input.DobVerified, input.Sex, input.Ethnicity,
            Clean(input.PreferredLanguage), input.DeceasedDate, input.RowVersion), ct).ConfigureAwait(false);
        await data.SaveRacesAsync(id, input.RaceCodes, ct).ConfigureAwait(false);
        await (saved.IsNew ? audit.CreatedAsync("person", id, ct) : audit.ChangedAsync("person", id, ct)).ConfigureAwait(false);

        var changes = new List<string>();
        if (saved.DobChanged)
        {
            changes.AddRange(await classification.RerunForPersonAsync(id, ct).ConfigureAwait(false));
        }

        if (saved.DeceasedChanged)
        {
            var open = (await data.GetCasesAsync(id, ct).ConfigureAwait(false)).Where(c => c.CaseStatus != "closed").ToList();
            if (open.Count > 0)
            {
                await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "deceasedReview",
                    $"Deceased: review {open.Count} open case(s)",
                    "Close with reason \"deceased\" after confirming: " + string.Join(", ", open.Select(c => c.CaseNumber)),
                    id, open[0].ExposureCaseId, null, null, null, false, today.AddDays(7)), ct).ConfigureAwait(false);
                changes.Add("A task lists the open cases to close with reason \"deceased\".");
            }
        }

        if (saved.IsNew && MatchKeys.IsPlaceholderName(firstName))
        {
            changes.Add("The first name looks like a placeholder; it is flagged for review and never links automatically.");
        }

        return new PersonSaveOutcome(id, saved.IsNew, changes);
    }

    public async Task SaveContactMethodAsync(Guid personId, Guid? id, string contactType, string value, bool isPreferred,
                                             bool isDeleted, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var v = new Validator();
        v.When(contactType is not ("phone" or "email"), "ContactType", "Choose phone or email.");
        v.Required("ContactValue", value, "Number or address");
        if (contactType == "email" && !string.IsNullOrWhiteSpace(value))
        {
            v.When(!value.Contains('@', StringComparison.Ordinal) || value.Trim().Contains(' ', StringComparison.Ordinal),
                "ContactValue", "Enter a valid email address.");
        }

        if (contactType == "phone" && !string.IsNullOrWhiteSpace(value))
        {
            v.When(value.Count(char.IsAsciiDigit) is < 10 or > 15, "ContactValue", "Enter a phone number with area code.");
        }

        v.ThrowIfInvalid();
        await data.SaveContactMethodAsync(id ?? TimeOrderedId.New(), personId, contactType, value.Trim(), isPreferred, isDeleted, ct)
            .ConfigureAwait(false);
        await audit.ChangedAsync("person", personId, ct).ConfigureAwait(false);
    }

    public async Task SaveIdentifierAsync(Guid personId, string type, string value, string? facility, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonViewIdentifiers);
        new Validator().Required("IdentifierValue", value, "Identifier").ThrowIfInvalid();
        await data.SaveIdentifierAsync(TimeOrderedId.New(), personId, type, value.Trim(), Clean(facility), "staff",
            clock.Today, false, ct).ConfigureAwait(false);
        await audit.ChangedAsync("person", personId, ct).ConfigureAwait(false);
    }

    /// <summary>"Add relationship" picks an existing person or creates a contact-only person.</summary>
    public async Task AddRelationshipAsync(RelationshipInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var v = new Validator();
        v.Required("Role", input.Role, "Role");
        v.When(input.RelatedPersonId is null && string.IsNullOrWhiteSpace(input.NewLastName), "NewLastName",
            "Pick a person or enter the new person's last name.");
        v.ThrowIfInvalid();

        Guid related = input.RelatedPersonId ?? (await SaveAsync(new PersonInput(null, input.NewLastName, input.NewFirstName, null,
            input.NewDob, false, input.NewSex ?? "unknown", "unknown", null, null, [], null), ct).ConfigureAwait(false)).PersonId;
        if (related == input.PersonId)
        {
            throw new BusinessRuleException("A person cannot be related to themselves.");
        }

        await data.SaveRelationshipAsync(new RelationshipSave(TimeOrderedId.New(), input.PersonId, related, input.Role,
            input.IsPrimaryContact, input.HasLegalCustody, input.StartDate, null, input.Source,
            input.Source is "interview" or "caseManager" ? clock.Today : null, false, null), ct)
            .ConfigureAwait(false);
        await audit.ChangedAsync("person", input.PersonId, ct).ConfigureAwait(false);
    }

    public async Task EndRelationshipAsync(Guid personId, Guid relationshipId, DateOnly endDate, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var rel = (await data.GetRelationshipsAsync(personId, ct).ConfigureAwait(false))
            .FirstOrDefault(r => r.PersonRelationshipId == relationshipId && !r.IsInverse) ?? throw new NotFoundException();
        await data.SaveRelationshipAsync(new RelationshipSave(relationshipId, personId, rel.OtherPersonId, rel.Role, false,
            rel.HasLegalCustody, rel.StartDate, endDate, rel.Source, rel.VerifiedDate, false, rel.RowVersion), ct).ConfigureAwait(false);
        await audit.ChangedAsync("person", personId, ct).ConfigureAwait(false);
    }

    public async Task SaveEmploymentAsync(EmploymentInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var existing = input.EmploymentId is Guid employmentId
            ? (await data.GetEmploymentAsync(input.PersonId, ct).ConfigureAwait(false)).FirstOrDefault(e => e.EmploymentId == employmentId) ?? throw new NotFoundException()
            : null;
        var v = new Validator();
        v.When(existing is not null && input.RowVersion?.Length != 8, "RowVersion", "Reload the job before editing it.");
        v.When(input.OrganizationId is null && string.IsNullOrWhiteSpace(input.NewOrganizationName), "OrganizationId",
            "Pick the employer or enter its name.");
        v.When(input.OccupationCode is { Length: > 4 }, "OccupationCode", "Census occupation codes are up to four digits.");
        v.When(input.NaicsOverride is { Length: > 6 }, "NaicsOverride", "NAICS codes are up to six digits.");
        v.When(input.EndDate is not null && input.StartDate is not null && input.EndDate < input.StartDate, "EndDate",
            "The end date is before the start date.");
        v.When(input.HoursPerWeek is < 0 or > 168, "HoursPerWeek", "Hours per week must be between 0 and 168.");
        if (input.WorksiteAddress is { } wa)
        {
            PlaceService.ValidateAddress(wa, v);
        }

        v.ThrowIfInvalid();

        if (existing is not null && !existing.RowVersion.SequenceEqual(input.RowVersion!)) throw new ConcurrencyException();
        Guid orgId = input.OrganizationId ?? await CreateOrganizationAsync(input.NewOrganizationName!, ["employer"], ct).ConfigureAwait(false);
        Guid? worksite = input.WorksiteLocationId ?? existing?.WorksiteLocationId;
        if (input.WorksiteAddress is { } addr)
        {
            worksite = await placeService.SaveAddressAsync(addr, ct).ConfigureAwait(false);
        }

        await places.SaveEmploymentAsync(new EmploymentSave(input.EmploymentId ?? TimeOrderedId.New(), input.PersonId, orgId, worksite,
            Clean(input.JobTitle), Clean(input.IndustryText), Clean(input.OccupationCode), input.OccupationCode is null ? null
                : existing is not null && input.OccupationCode == existing.OccupationCode ? existing.OccupationCodeVersion : "2002",
            Clean(input.NaicsOverride), Clean(input.MetalTasks), Clean(input.PpeNotes), input.HoursPerWeek, input.StartDate,
            input.EndDate, input.Source, input.Verify ? clock.Today : existing?.VerifiedDate, input.AnalyteIds, false, input.RowVersion), ct).ConfigureAwait(false);
        await audit.ChangedAsync("person", input.PersonId, ct).ConfigureAwait(false);
    }

    public async Task SaveExposureSourceAsync(ExposureSourceInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var existing = input.ExposureSourceId is Guid sourceId
            ? (await data.GetExposureSourcesAsync(input.PersonId, ct).ConfigureAwait(false)).FirstOrDefault(s => s.ExposureSourceId == sourceId) ?? throw new NotFoundException()
            : null;
        new Validator().Required("SourceType", input.SourceType, "Source type")
            .When(input.EndDate is not null && input.StartDate is not null && input.EndDate < input.StartDate, "EndDate", "The end date is before the start date.")
            .When(existing is not null && input.OriginalModifiedUtc is null, "OriginalModifiedUtc", "Reload the source before editing it.").ThrowIfInvalid();
        if (existing is not null && existing.ModifiedDateUtc != input.OriginalModifiedUtc) throw new ConcurrencyException();
        Guid? productId = string.IsNullOrWhiteSpace(input.ProductName) ? null : existing?.ProductId;
        if (!string.IsNullOrWhiteSpace(input.ProductName) && (existing is null || input.ProductName != existing.ProductName || input.ProductBrand != existing.Brand || input.ProductCountry != existing.ProductCountry))
        {
            productId = TimeOrderedId.New();
            await places.SaveProductAsync(productId.Value, Clean(input.ProductBrand), input.ProductName.Trim(),
                Clean(input.ProductCountry)?.ToUpperInvariant(), null, null, ct).ConfigureAwait(false);
        }

        var open = (await data.GetCasesAsync(input.PersonId, ct).ConfigureAwait(false)).FirstOrDefault(c => c.CaseStatus != "closed");
        await places.SaveExposureSourceAsync(new ExposureSourceSave(input.ExposureSourceId ?? TimeOrderedId.New(), input.PersonId, existing is null ? open?.ExposureCaseId : existing.ExposureCaseId,
            input.SourceType, Clean(input.Description), productId, existing?.LocationId, input.StartDate, input.EndDate, input.IsConfirmed, false, input.OriginalModifiedUtc), ct)
            .ConfigureAwait(false);
        await audit.ChangedAsync("person", input.PersonId, ct).ConfigureAwait(false);
    }

    /// <summary>Saves a pregnancy episode, warns rather than blocks for a person recorded as male, and reruns classification.</summary>
    public async Task<IReadOnlyList<string>> SavePregnancyAsync(PregnancyInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        var v = new Validator();
        v.When(input.ActualEndDate is not null && input.ActualEndDate < input.StartDate, "ActualEndDate",
            "The end date must be on or after the start date.");
        v.When(input.ExpectedEndDate is not null && input.ExpectedEndDate < input.StartDate, "ExpectedEndDate",
            "The expected end must be on or after the start date.");
        v.ThrowIfInvalid();

        var header = await data.GetPersonAsync(input.PersonId, ct).ConfigureAwait(false);
        var notes = new List<string>();
        if (header.Person.Sex == "male")
        {
            notes.Add("This person is recorded as male. The episode was saved; please confirm the record.");
        }

        await data.SavePregnancyAsync(new PregnancySave(input.PregnancyEpisodeId ?? TimeOrderedId.New(), input.PersonId,
            input.StartDate, input.ExpectedEndDate, input.ActualEndDate, Clean(input.Outcome), input.Source, null, false,
            input.RowVersion), ct).ConfigureAwait(false);
        await audit.ChangedAsync("person", input.PersonId, ct).ConfigureAwait(false);

        if (input.Outcome == "liveBirth")
        {
            await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "newbornTesting", "Link the newborn and arrange testing",
                "A live birth was recorded. Add the newborn as a contact-only person with a parent relationship.", input.PersonId,
                null, null, null, null, false, clock.Today.AddDays(14)), ct).ConfigureAwait(false);
            notes.Add("A newborn testing task was created.");
        }

        notes.AddRange(await classification.RerunForPersonAsync(input.PersonId, ct).ConfigureAwait(false));
        _ = cases;
        return notes;
    }

    public async Task<Guid> CreateOrganizationAsync(string name, IReadOnlyList<string> types, CancellationToken ct = default)
    {
        session.Require(Permissions.PersonEdit);
        Guid id = TimeOrderedId.New();
        await places.SaveOrganizationAsync(new OrganizationSave(id, name.Trim(), MatchKeys.NormalizeWords(name), null, null, null, null,
            null, null, null, null, true, types, null), ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>A guardian value that looks like an organization goes to review as a possible employer.</summary>
    public static bool LooksLikeOrganization(string? value)
    {
        var words = MatchKeys.NormalizeWords(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => OrgSuffixes.Contains(w));
    }

    public static bool IsValidNpi(string? npi) => ProviderIdentifiers.IsValidNpi(npi);

    private async Task<T> Read<T>(Func<Task<T>> f)
    {
        session.Require(Permissions.PersonView);
        return await f().ConfigureAwait(false);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
