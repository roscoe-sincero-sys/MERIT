using System.Globalization;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Reference;
using Merit.Application.Services;
using Merit.Domain.Classification;
using Merit.Domain.Ids;
using Merit.Domain.Identifiers;
using Merit.Domain.Matching;
using Merit.Domain.Results;
using Merit.Domain.Text;
using Merit.Domain.Thresholds;
using Microsoft.Extensions.Logging;
using Merit.Application.Security;

namespace Merit.Application.Intake;

public sealed record IntakeRunSummary(int Received, int Loaded, int Held, int Rejected, bool Paused, string? PauseReason);

/// <summary>
/// The intake pipeline, run by the worker for one batch: receive (already stored), parse with the sender's profile,
/// validate, normalize, match, then commit clean rows and hold the rest with named reasons, and classify each loaded row.
/// Apart from a layout pause a batch never fails whole. Logs carry record ids only, never patient data.
/// </summary>
public sealed partial class IntakeProcessor(
    IClock clock,
    IIntakeData intake,
    IPlaceData places,
    IPersonData persons,
    IResultData results,
    IWorkData work,
    IReferenceDataProvider reference,
    IThresholdProvider thresholds,
    ISettingsProvider settings,
    PersonService personService,
    PlaceService placeService,
    ClassificationService classification,
    ILogger<IntakeProcessor> logger)
{
    public async Task<IntakeRunSummary> ProcessAsync(Guid batchId, bool releasedOnly, CancellationToken ct = default)
    {
        var batch = await intake.GetBatchAsync(batchId, true, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Batch {batchId} not found.");
        var profile = batch.IntakeProfileId is Guid pid ? await intake.GetProfileAsync(pid, ct).ConfigureAwait(false) : null;
        if (profile is null)
        {
            await intake.SetBatchStatusAsync(batchId, "failed", "The batch has no intake profile.", ct).ConfigureAwait(false);
            return new IntakeRunSummary(0, 0, 0, 0, false, "No profile");
        }

        await intake.SetBatchStatusAsync(batchId, "parsing", null, ct).ConfigureAwait(false);

        if (!releasedOnly)
        {
            if (profile.FileType == "hl7")
            {
                IReadOnlyList<Dictionary<string, string?>> observations;
                try { observations = Hl7Reader.Read(batch.RawContent ?? string.Empty, profile.SenderClia); }
                catch (Hl7ParseException ex)
                {
                    await intake.SetBatchStatusAsync(batchId, "paused", ex.Message, ct).ConfigureAwait(false);
                    await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "pausedBatch",
                        "HL7 intake batch needs review", ex.Message, null, null, null, null, null, false, clock.Today.AddDays(2)), ct).ConfigureAwait(false);
                    return new IntakeRunSummary(0, 0, 0, 0, true, ex.Message);
                }

                var nativeConstants = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.ConstantsJson ?? "{}") ?? [];
                var nativeTranslations = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(profile.ValueTranslationsJson ?? "{}") ?? [];
                var nativeRows = observations.Select((fields, index) =>
                {
                    foreach (var (key, value) in fields.ToArray())
                        if (value is not null && nativeTranslations.TryGetValue(key, out var translations) && translations.TryGetValue(value, out var translated)) fields[key] = translated;
                    foreach (var (key, value) in nativeConstants)
                        if (!fields.TryGetValue(key, out var existing) || string.IsNullOrEmpty(existing)) fields[key] = value;
                    return new StagingRowNew(TimeOrderedId.New(), index + 1, JsonSerializer.Serialize(fields));
                }).ToArray();
                foreach (var chunk in nativeRows.Chunk(200)) await intake.SaveStagingRowsAsync(batchId, chunk, ct).ConfigureAwait(false);
            }
            else
            {
                var parsed = CsvReader.Read(batch.RawContent ?? string.Empty, CsvReader.DelimiterFor(profile.FileType));
                int headerIndex = Math.Max(profile.HeaderRow - 1, 0);
                if (parsed.Count <= headerIndex)
                {
                    await intake.SetBatchStatusAsync(batchId, "loaded", "The file has no rows.", ct).ConfigureAwait(false);
                    return new IntakeRunSummary(0, 0, 0, 0, false, null);
                }

                var header = parsed[headerIndex].Select(h => h.Trim()).ToList();
                string signature = string.Join(',', header);
                if (!string.IsNullOrWhiteSpace(profile.HeaderSignature)
                    && !string.Equals(Normalize(profile.HeaderSignature), Normalize(signature), StringComparison.OrdinalIgnoreCase))
                {
                    // Layout drift: pause the whole batch before loading, rather than put values in the wrong fields.
                    string reason = "The sender's columns differ from its intake profile. Update the mapping, then resume the batch.";
                    await intake.SetBatchStatusAsync(batchId, "paused", reason, ct).ConfigureAwait(false);
                    await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "pausedBatch",
                        $"Intake batch paused: {batch.FileName}", reason, null, null, null, null, null, false,
                        clock.Today.AddDays(2)), ct).ConfigureAwait(false);
                    return new IntakeRunSummary(parsed.Count - headerIndex - 1, 0, 0, 0, true, reason);
                }

                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.ColumnMapJson) ?? [];
                var shared = SharedTargets(header, map);
                if (shared.Count > 0)
                {
                    // Two columns into one field: the later would silently overwrite the earlier, so pause instead.
                    string reason = "The intake profile maps more than one column to the same field ("
                        + string.Join("; ", shared.Select(s => $"{string.Join(" and ", s.Columns)} to {DisplayNames.Field(s.Field)}"))
                        + "). Correct the mapping, then resume the batch.";
                    await intake.SetBatchStatusAsync(batchId, "paused", reason, ct).ConfigureAwait(false);
                    await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "pausedBatch",
                        $"Intake batch paused: {batch.FileName}", reason, null, null, null, null, null, false,
                        clock.Today.AddDays(2)), ct).ConfigureAwait(false);
                    return new IntakeRunSummary(parsed.Count - headerIndex - 1, 0, 0, 0, true, reason);
                }

                var translations = string.IsNullOrWhiteSpace(profile.ValueTranslationsJson)
                    ? new Dictionary<string, Dictionary<string, string>>()
                    : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(profile.ValueTranslationsJson) ?? [];
                var constants = string.IsNullOrWhiteSpace(profile.ConstantsJson)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(profile.ConstantsJson) ?? [];

                var staged = new List<StagingRowNew>();
                for (int r = headerIndex + 1; r < parsed.Count; r++)
                {
                    var fields = StageFields(header, parsed[r], map, translations, constants);
                    staged.Add(new StagingRowNew(TimeOrderedId.New(), r - headerIndex, JsonSerializer.Serialize(fields)));
                }

                foreach (var chunk in staged.Chunk(200))
                {
                    await intake.SaveStagingRowsAsync(batchId, chunk, ct).ConfigureAwait(false);
                }
            }
        }

        var rows = await intake.GetRowsForProcessingAsync(batchId, releasedOnly, ct).ConfigureAwait(false);
        var registry = new RegistryLookups(places);
        int loaded = 0, held = 0;
        var formats = ((profile.FileType == "hl7" ? "yyyy-MM-dd," : "") + (profile.DateFormats ?? "yyyy-MM-dd,MM/dd/yyyy,M/d/yyyy"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    bool ok = await ProcessRowAsync(batch, profile, row, formats, registry, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        loaded++;
                    }
                    else
                    {
                        held++;
                    }
                }
                catch (Errors.MeritException ex)
                {
                    held++;
                    LogRowHeld(logger, row.StagingRowId, ex.GetType().Name);
                    await intake.CompleteRowAsync(row.StagingRowId, "held", null,
                        [new HoldReason(ex is Errors.PermissionDeniedException ? "permissionDenied" : "uncertainMatch", null,
                            ex is Errors.PermissionDeniedException ? "The initiating profile lacks permission required for this import. Ask an administrator to review the sender and account setup. " + ex.Message : ex.Message, null)], false, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Issue 269: a row that cannot even be held must not strand the batch in 'parsing' with nothing to review.
            // Rows already loaded or held stay as they are; the uploader sees the batch as failed with a reason.
            LogBatchFailed(logger, batchId, ex.GetType().Name);
            try
            {
                await intake.SetBatchStatusAsync(batchId, "failed",
                    $"Processing stopped after {loaded + held} of {rows.Count} rows: {(ex is Errors.MeritException ? ex.Message : "an unexpected error occurred")}. "
                    + "Rows already loaded or held are kept. Ask an administrator to review the job log before uploading the remaining rows again.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Errors.MeritException)
            {
                // The original failure is what the job records; a second failure here must not replace it.
            }

            throw;
        }

        await intake.SetBatchStatusAsync(batchId, "loaded", null, ct).ConfigureAwait(false);
        var final = await intake.GetBatchAsync(batchId, false, ct).ConfigureAwait(false);
        return new IntakeRunSummary(final?.ReceivedCount ?? rows.Count, final?.LoadedCount ?? loaded, final?.HeldCount ?? held,
            final?.RejectedCount ?? 0, false, null);
    }

    private async Task<bool> ProcessRowAsync(IntakeBatchRow batch, IntakeProfileRow profile, StagingRowForProcessing row,
                                             string[] formats, RegistryLookups registry, CancellationToken ct)
    {
        var f = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.ParsedJson) ?? [];
        if (!string.IsNullOrWhiteSpace(row.CorrectedJson))
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string?>>(row.CorrectedJson) ?? [])
            {
                f[k] = v;
            }
        }

        string? Get(string key) => f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var refData = await reference.GetAsync(ct).ConfigureAwait(false);
        var config = await settings.GetAsync(ct).ConfigureAwait(false);
        var holds = new List<HoldReason>();
        var today = clock.Today;

        // ---- sender and laboratory
        string? clia = ProviderIdentifiers.NormalizeClia(Get("labCliaNumber"));
        Guid? labId = await registry.LaboratoryAsync(clia, ct).ConfigureAwait(false) ?? profile.SenderOrganizationId;
        if (labId is null)
        {
            holds.Add(new HoldReason("unknownSender", "labCliaNumber", "The performing laboratory is not in the reporter registry.", clia));
        }

        // ---- test code -> analyte specimen
        string? loinc = Get("loinc");
        var loincRow = refData.Loinc(loinc);
        AnalyteSpecimenRow? specimen = loincRow is null ? null : refData.Specimen(loincRow.AnalyteSpecimenId);
        if (specimen is null)
        {
            holds.Add(new HoldReason("unknownTestCode", "loinc", $"Test code {loinc ?? "(blank)"} is not mapped to an analyte.", loinc));
        }
        else if (refData.Analyte(specimen.AnalyteId) is { IsActive: false })
        {
            holds.Add(new HoldReason("inactiveAnalyte", "loinc", "The analyte for this test code is inactive.", loinc));
        }

        // ---- value and unit
        var parsed = ResultNormalizer.Parse(Get("value"));
        if (parsed.Value is null)
        {
            holds.Add(new HoldReason("missingResultValue", "value", "The result value is missing or not a number.", Get("value")));
        }

        string? unitText = Get("unit") ?? specimen?.CanonicalUnit;
        AcceptedUnit? unit = specimen is null ? null : ResultNormalizer.FindUnit(unitText, refData.DomainUnitsFor(specimen.AnalyteSpecimenId));
        if (specimen is not null && unit is null)
        {
            holds.Add(new HoldReason("unknownUnit", "unit", $"Unit {unitText ?? "(blank)"} is not accepted for this test.", unitText));
        }

        // ---- dates
        DateOnly? dob = ParseDate(Get("dob"), formats);
        DateOnly? collected = ParseDate(Get("collectedDate"), formats);
        DateOnly? reported = ParseDate(Get("reportedDate"), formats);
        if (batch.DepartmentReceivedUtc is DateTime originalReceipt)
        {
            var receiptDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(originalReceipt, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(batch.ReceiptTimeZone ?? clock.Zone.Id)));
            var historicalFinalText = Get("finalResultDate"); var finalDay = ParseDate(historicalFinalText, formats);
            if (collected > receiptDay || reported > receiptDay || (historicalFinalText is not null && (finalDay is null || finalDay > receiptDay || finalDay < collected)))
                holds.Add(new HoldReason("impossibleDates", "collectedDate", "Verified Department receipt precedes collection/report/final-result date, or the final-result date is invalid. Review the original source and receipt evidence; do not replace receipt with upload time.", Get("collectedDate")));
        }
        if (collected is null || collected > today || (reported is not null && reported < collected)
            || (dob is not null && collected < dob) || (dob is not null && dob > today))
        {
            holds.Add(new HoldReason("impossibleDates", "collectedDate",
                "The collection date is missing, in the future, before the date of birth or after the reported date.", Get("collectedDate")));
        }

        if (dob is null)
        {
            holds.Add(new HoldReason("missingDob", "dob", "The date of birth is missing or unreadable.", Get("dob")));
        }

        string? lastName = Get("lastName");
        string? firstName = Get("firstName");
        if (lastName is null)
        {
            holds.Add(new HoldReason("uncertainMatch", "lastName", "The patient's last name is missing.", null));
        }

        // ---- normalize
        NormalizedValue? normalized = parsed.Value is decimal value && unit is not null && specimen is not null
            ? ResultNormalizer.Normalize(parsed.Operator, value, unit, specimen.CanonicalUnit, null, false)
            : null;
        string? sampleType = specimen is { UsesSampleType: true } ? (loincRow?.ImpliedSampleType ?? Get("sampleType") ?? "unknown") : null;
        string? speciation = loincRow?.ImpliedSpeciation ?? Get("speciation");

        // ---- match
        Guid? personId = null;
        bool newPerson = false;
        // A reviewer's decision on the matching review screen, stored beside the reported values, settles the match.
        if (Guid.TryParse(Get("matchPersonId"), out var decidedPersonId))
        {
            personId = decidedPersonId;
        }
        else if (Get("matchNewPerson") == "true" && lastName is not null && dob is not null)
        {
            newPerson = true;
        }
        else if (lastName is not null && dob is not null)
        {
            string lastKey = MatchKeys.NameKey(lastName)!;
            string? firstKey = MatchKeys.NameKey(firstName);
            string sex = Get("sex") ?? "unknown";
            // One row per person: the procedure can find the same person by name, alias and first name with DOB. People a
            // reviewer called a different person for this identity are left out.
            var candidates = (await persons.FindCandidatesAsync(lastKey, firstKey, dob, 10,
                    MatchComparer.IncomingKey(lastName, firstName, dob), ct).ConfigureAwait(false))
                .DistinctBy(c => c.PersonId).ToList();
            bool placeholder = MatchKeys.IsPlaceholderName(firstName);
            var exact = candidates.Where(c => c.LastNameKey == lastKey && c.FirstNameKey == firstKey && c.Dob == dob
                                              && (c.Sex == sex || c.Sex == "unknown" || sex == "unknown") && !c.IsPlaceholderName)
                .ToList();
            if (!placeholder && exact.Count == 1)
            {
                personId = exact[0].PersonId;
            }
            else if (candidates.Count == 0 && !placeholder)
            {
                newPerson = true;
            }
            else
            {
                holds.Add(new HoldReason("uncertainMatch", "lastName",
                    placeholder
                        ? "Placeholder first name (for example BABY BOY): never linked automatically. Review the match."
                        : $"{(candidates.Count == 1 ? "1 possible match needs" : $"{candidates.Count} possible matches need")} review; strict matching links only on exact name, DOB and sex.",
                    null));
            }
        }

        if (holds.Count > 0)
        {
            bool urgent = false;
            if (normalized is not null && specimen is not null && collected is not null)
            {
                var rules = await thresholds.GetRulesAsync(specimen.AnalyteSpecimenId, collected.Value, ct).ConfigureAwait(false);
                decimal? lowestAction = ThresholdEvaluator.LowestActionLevel(rules) ?? ThresholdEvaluator.LowestCaseLevel(rules);
                urgent = lowestAction is not null && normalized.ValueCanonical >= lowestAction && normalized.Operator != ResultOperator.LessThan;
            }

            await intake.CompleteRowAsync(row.StagingRowId, "held", null, holds, urgent, ct).ConfigureAwait(false);
            return false;
        }

        // ---- commit: person, alias, address, guardian
        if (newPerson)
        {
            // #279/#280: the reported residence county decides which of the worker's jurisdictions owns the new patient.
            var saved = await personService.SaveAsync(new PersonInput(null, lastName, firstName, Get("middleName"), dob, false,
                Get("sex") ?? "unknown", "unknown", null, null, [], null, Get("countyFips")), ct).ConfigureAwait(false);
            personId = saved.PersonId;
        }

        await persons.SaveAliasAsync(new AliasSave(personId!.Value, lastName!, firstName, Get("middleName"),
            MatchKeys.NameKey(lastName)!, MatchKeys.NameKey(firstName),
            MatchKeys.IsPlaceholderName(firstName) ? "placeholder" : "asReported", "labReport", null, collected!.Value), ct).ConfigureAwait(false);

        if (profile.FileType == "hl7" && Get("hl7PatientIdentifiersJson") is not null)
            await intake.PersistIdentifiersAsync(row.StagingRowId,personId.Value,ct).ConfigureAwait(false);

        if(Get("phone")?.Trim() is string phone && phone.Length<=200 && phone.Count(char.IsAsciiDigit) is >=10 and <=15)
        {
            var header=await persons.GetPersonAsync(personId.Value,ct).ConfigureAwait(false);
            if(!header.ContactMethods.Any(m=>m.ContactType=="phone" && m.ContactValue==phone))
                await personService.SaveContactMethodAsync(personId.Value,null,"phone",phone,false,false,ct).ConfigureAwait(false);
        }

        Guid? reportedLocationId = await HandleAddressAsync(personId.Value, f, collected.Value, config, ct).ConfigureAwait(false);
        var guardians=Get("guardianContactsJson") is string guardianJson
            ? JsonSerializer.Deserialize<Dictionary<string,string?>[]>(guardianJson) ?? [] : [f];
        foreach(var guardian in guardians) await HandleGuardianAsync(personId.Value,guardian,collected.Value,config,ct,Get("countyFips")).ConfigureAwait(false);

        Guid? providerId = null;
        // The same normalization as the registry, so a formatted NPI in the file still links (issue #12).
        string? npi = ProviderIdentifiers.NormalizeNpi(Get("orderingProviderNpi"));
        if (ProviderIdentifiers.IsValidNpi(npi))
        {
            providerId = await registry.ProviderAsync(npi!, ct).ConfigureAwait(false);
        }

        // ---- duplicates and corrections
        string status = Get("resultStatus") ?? "final";
        var duplicate = await results.FindDuplicateAsync(personId.Value, specimen!.AnalyteSpecimenId, collected.Value, labId!.Value,
            Get("accessionNumber"), normalized!.ValueReported, ct,
            sourceObservationKey: profile.FileType == "hl7" ? Get("hl7ObservationKey") : null).ConfigureAwait(false);
        bool supersedes = duplicate is not null && status is "corrected" or "cancelled";
        bool isDuplicate = duplicate is not null && !supersedes && duplicate.ResultRowStatus == "valid";

        string reportedSnapshot = row.ParsedJson;
        if (profile.FileType == "hl7")
        {
            // External identifiers belong in the permission-filtered person identifier collection.
            // Keep the complete source in intake, without exposing PID-3 through ordinary result.view.
            var reportedFields = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.ParsedJson) ?? [];
            reportedFields.Remove("hl7PatientIdentifiersJson");
            reportedSnapshot = JsonSerializer.Serialize(reportedFields);
        }
        if (Get("finalResultDate") is string finalText)
        {
            var finalSnapshot = JsonSerializer.Deserialize<Dictionary<string, string?>>(reportedSnapshot) ?? [];
            finalSnapshot["finalResultDateCanonical"] = ParseDate(finalText, formats)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            reportedSnapshot = JsonSerializer.Serialize(finalSnapshot);
        }
        Guid resultId = TimeOrderedId.New();
        if (batch.DepartmentReceivedUtc is not null)
        {
            var provenance = JsonSerializer.Deserialize<Dictionary<string, string?>>(reportedSnapshot) ?? [];
            provenance["departmentReceiptSource"] = "verifiedHistoricalBatch";
            provenance["departmentReceiptBatchId"] = batch.IntakeBatchId.ToString();
            provenance["departmentReceiptUtc"] = batch.DepartmentReceivedUtc.Value.ToString("O", CultureInfo.InvariantCulture);
            provenance["departmentReceiptTimeZone"] = batch.ReceiptTimeZone;
            provenance["departmentReceiptEvidence"] = batch.ReceiptEvidence;
            reportedSnapshot = JsonSerializer.Serialize(provenance);
        }
        await results.SaveLabResultAsync(new LabResultSave
        {
            LabResultId = resultId,
            PersonId = personId,
            AnalyteSpecimenId = specimen.AnalyteSpecimenId,
            CollectedDate = collected.Value,
            ReportedDate = reported,
            ReceivedUtc = batch.DepartmentReceivedUtc ?? batch.ReceivedUtc,
            ResultStatus = status,
            SupersedesLabResultId = supersedes ? duplicate!.LabResultId : null,
            ValueReported = normalized.ValueReported,
            ValueText = parsed.RawText,
            Operator = normalized.Operator.ToCode(),
            UnitReported = unitText!,
            ValueCanonical = normalized.ValueCanonical,
            UnitCanonical = normalized.UnitCanonical,
            SampleType = sampleType,
            TestMethod = Get("testMethod"),
            Speciation = speciation,
            LabOrganizationId = labId.Value,
            OrderingProviderId = providerId,
            AccessionNumber = Get("accessionNumber"),
            LoincReported = loinc,
            ReportedSnapshotJson = reportedSnapshot,
            ReportedLocationId = reportedLocationId,
            ResultRowStatus = isDuplicate ? "duplicate" : "valid",
            IntakeBatchId = batch.IntakeBatchId,
        }, ct).ConfigureAwait(false);

        if (!isDuplicate && status != "cancelled")
        {
            await classification.ClassifyAsync(resultId, personId.Value, specimen.AnalyteSpecimenId,
                new ClassifiableResult(resultId, collected.Value, sampleType, Get("testMethod"), speciation, normalized.Operator,
                    normalized.ValueCanonical, status != "preliminary"), ct).ConfigureAwait(false);
        }

        await intake.CompleteRowAsync(row.StagingRowId, "loaded", resultId, [], false, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Address rules 4 and 5: with no residence, the lab-reported address becomes the primary (reason "lab-reported
    /// address", unverified); a different address on a later report creates an address review task instead of moving
    /// the primary, unless the jurisdiction's policy says to move it.
    /// </summary>
    private async Task<Guid?> HandleAddressAsync(Guid personId, Dictionary<string, string?> f, DateOnly collected,
                                                 JurisdictionSettings config, CancellationToken ct)
    {
        string? line1 = f.GetValueOrDefault("addressLine1");
        string? city = f.GetValueOrDefault("city");
        if (string.IsNullOrWhiteSpace(line1) || string.IsNullOrWhiteSpace(city))
        {
            return null;
        }

        var address = new AddressInput(line1, f.GetValueOrDefault("addressUnit"), city, f.GetValueOrDefault("state"), f.GetValueOrDefault("zip5"),
            CountyFips: f.GetValueOrDefault("countyFips"));
        var v = new Errors.Validator();
        PlaceService.ValidateAddress(address, v);
        if (!v.IsValid)
        {
            return null;
        }

        Guid locationId = await placeService.SaveAddressAsync(address, ct).ConfigureAwait(false);
        var current = (await persons.GetPlacesAsync(personId, ct).ConfigureAwait(false))
            .Where(p => p.Role == "residence" && (p.EndDate is null || p.EndDate >= collected)).ToList();

        if (current.Count == 0)
        {
            await placeService.AddPlaceAsync(new AddPlaceInput(personId, "residence", locationId, null, null, null, null, null,
                collected, null, "labReport", true, "labReportedAddress"), false, ct).ConfigureAwait(false);
        }
        else if (!current.Any(p => p.LocationId == locationId))
        {
            var primary = current.FirstOrDefault(p => p.IsPrimary);
            if (config.NewLabAddressMovesPrimary && primary is not null)
            {
                await placeService.AddPlaceAsync(new AddPlaceInput(personId, "residence", locationId, null, null, null, null, null,
                    collected > primary.StartDate ? collected : primary.StartDate.AddDays(1), null, "labReport", true,
                    "labReportedAddress"), false, ct).ConfigureAwait(false);
            }
            else
            {
                await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "addressReview", "New address on a lab report",
                    $"A result collected {collected.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} reports {PlaceService.FormatAddress(address)}, which differs from the primary residence"
                    + (primary is null ? "." : $" ({primary.AddressLabel})."), personId, null, null, null, null, false,
                    clock.Today.AddDays(14)), ct).ConfigureAwait(false);
            }
        }

        return locationId;
    }

    /// <summary>
    /// A guardian on the report becomes a contact-only person with an unverified relationship; a guardian value that
    /// looks like an organization (an employer typed into the guardian field) goes to review instead.
    /// </summary>
    private async Task HandleGuardianAsync(Guid personId, Dictionary<string,string?> fields, DateOnly collected, JurisdictionSettings config, CancellationToken ct,
                                           string? patientCountyFips = null)
    {
        string? guardian=fields.GetValueOrDefault("guardianName");
        if (string.IsNullOrWhiteSpace(guardian))
        {
            return;
        }

        if (PersonService.LooksLikeOrganization(guardian))
        {
            await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "crossLink", "Guardian field looks like an organization",
                "The report's guardian value looks like an organization name; it may be the person's employer. Review and record it as employment if so.",
                personId, null, null, null, null, false, clock.Today.AddDays(14)), ct).ConfigureAwait(false);
            return;
        }

        string last, first;
        if (guardian.Contains(',', StringComparison.Ordinal))
        {
            var parts = guardian.Split(',', 2, StringSplitOptions.TrimEntries);
            (last, first) = (parts[0], parts[1]);
        }
        else
        {
            var parts = guardian.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            (first, last) = parts.Length > 1 ? (string.Join(' ', parts[..^1]), parts[^1]) : (string.Empty, parts[0]);
        }

        async Task Review() => await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(),"crossLink","Verify laboratory caregiver details",
            "Verify reported caregiver identity, relationship and contact information against the source. Laboratory reporting does not establish legal custody. Update and verify the relationship in the patient workspace.",personId,null,null,null,null,false,clock.Today.AddDays(7)),ct);
        if(string.IsNullOrWhiteSpace(last)) { await Review(); return; }
        var existing = await persons.GetRelationshipsAsync(personId, ct).ConfigureAwait(false);
        var matches=existing.Where(r=>!r.IsInverse && r.EndDate is null && MatchKeys.NameKey(r.OtherLastName)==MatchKeys.NameKey(last) && MatchKeys.NameKey(r.OtherFirstName)==MatchKeys.NameKey(first)).ToList();
        if(matches.Count==0)
        {
            await personService.AddRelationshipAsync(new RelationshipInput(personId, null, last, string.IsNullOrEmpty(first) ? null : first,
                null, null, "caregiver", false, false, null, "labReport", patientCountyFips), ct).ConfigureAwait(false);
            matches=(await persons.GetRelationshipsAsync(personId,ct)).Where(r=>!r.IsInverse && r.EndDate is null && MatchKeys.NameKey(r.OtherLastName)==MatchKeys.NameKey(last) && MatchKeys.NameKey(r.OtherFirstName)==MatchKeys.NameKey(first)).ToList();
        }
        if(matches.Count!=1) { await Review(); return; }
        var related=matches[0].OtherPersonId;
        var header=await persons.GetPersonAsync(related,ct).ConfigureAwait(false);
        foreach(var (key,type) in new[] {("guardianPhone","phone"),("guardianEmail","email")})
        {
            var value=fields.GetValueOrDefault(key)?.Trim();
            if(string.IsNullOrWhiteSpace(value) || header.ContactMethods.Any(m=>m.ContactType==type && m.ContactValue==value)) continue;
            bool valid=value.Length<=200 && (type=="phone" ? value.Count(char.IsAsciiDigit) is >=10 and <=15 : value.Contains('@',StringComparison.Ordinal) && !value.Contains(' ',StringComparison.Ordinal));
            if(valid) await personService.SaveContactMethodAsync(related,null,type,value,false,false,ct).ConfigureAwait(false);
        }
        if(!string.IsNullOrWhiteSpace(fields.GetValueOrDefault("guardianAddressLine1")) && !string.IsNullOrWhiteSpace(fields.GetValueOrDefault("guardianCity")))
        {
            var address=new Dictionary<string,string?> { ["addressLine1"]=fields.GetValueOrDefault("guardianAddressLine1"),["city"]=fields.GetValueOrDefault("guardianCity"),
                ["state"]=fields.GetValueOrDefault("guardianState"),["zip5"]=fields.GetValueOrDefault("guardianZip5") };
            await HandleAddressAsync(related,address,collected,config,ct).ConfigureAwait(false);
        }
        if(matches[0].VerifiedDate is null) await Review();
    }

    private static DateOnly? ParseDate(string? text, string[] formats)
        => text is not null && DateOnly.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static string Normalize(string header)
        => string.Join(',', header.Split(',').Select(h => h.Trim()));

    /// <summary>One file row as staged fields: each mapped column's trimmed, translated value (blank is null), then the profile's constants.</summary>
    public static Dictionary<string, string?> StageFields(IReadOnlyList<string> header, IReadOnlyList<string> row,
                                                          IReadOnlyDictionary<string, string> map,
                                                          IReadOnlyDictionary<string, Dictionary<string, string>> translations,
                                                          IReadOnlyDictionary<string, string> constants)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int c = 0; c < header.Count && c < row.Count; c++)
        {
            if (map.TryGetValue(header[c], out var target))
            {
                string value = row[c].Trim();
                if (translations.TryGetValue(target, out var t) && t.TryGetValue(value, out var translated))
                {
                    value = translated;
                }

                fields[target] = value.Length == 0 ? null : value;
            }
        }

        foreach (var (k, v) in constants)
        {
            fields.TryAdd(k, v);
        }

        return fields;
    }

    /// <summary>
    /// The fields that more than one of the file's columns map to, each with its columns. Loading such a file would let
    /// the later column overwrite the earlier one, as an Apt column mapped to the result unit once put "ug/dL" into
    /// addresses (issue #157).
    /// </summary>
    public static IReadOnlyList<(string Field, IReadOnlyList<string> Columns)> SharedTargets(IReadOnlyList<string> header,
                                                                                            IReadOnlyDictionary<string, string> map)
        => header.Distinct(StringComparer.Ordinal).Where(map.ContainsKey)
            .GroupBy(h => map[h], StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.ToList()))
            .ToList();

    [LoggerMessage(Level = LogLevel.Information, Message = "Staging row {StagingRowId} held: {Reason}")]
    private static partial void LogRowHeld(ILogger logger, Guid stagingRowId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intake batch {IntakeBatchId} failed while processing rows: {Reason}")]
    private static partial void LogBatchFailed(ILogger logger, Guid intakeBatchId, string reason);
}
