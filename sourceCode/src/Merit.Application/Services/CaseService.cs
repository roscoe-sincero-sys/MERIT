using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Domain.Ids;

namespace Merit.Application.Services;

public sealed record ActivityInput(Guid ExposureCaseId, string ActivityType, DateOnly ActivityDate, Guid? ContactPersonId,
                                   string? Outcome, string? Note);

public sealed record InvestigationRequest(Guid ExposureCaseId, string InvestigationType, Guid? LocationId, Guid? OrganizationId,
                                          Guid? WorksiteLocationId, Guid? EmploymentId, Guid? JoinInvestigationId);

/// <summary>Case management: one person's elevated exposure to one analyte, from opening to closure.</summary>
public sealed class CaseService(IClock clock, ICaseData cases, IWorkData work, IAccountData accounts, ISettingsProvider settings,
                                AuditService audit, ICurrentSession session)
{
    private static readonly string[] ClosureReasons =
        ["belowLevelOnRetest", "lostToFollowUp", "movedOutOfState", "declined", "deceased", "duplicate", "other"];

    public async Task<CaseDetail> GetAsync(Guid exposureCaseId, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        var detail = await cases.GetCaseAsync(exposureCaseId, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        await audit.ViewedAsync("exposureCase", exposureCaseId, ct).ConfigureAwait(false);
        return detail;
    }

    public async Task<IReadOnlyList<CaseListRow>> ListAsync(CaseListQuery query, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        return await cases.ListCasesAsync(query, ct).ConfigureAwait(false);
    }

    /// <summary>The tenants that own a case the caller can see, for the list's owner filter.</summary>
    public async Task<IReadOnlyList<CaseOwnerRow>> OwnersAsync(CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        return await cases.ListCaseOwnersAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AssignableUserRow>> AssignableUsersAsync(CancellationToken ct = default)
        => accounts.ListAssignableUsersAsync(ct);

    public async Task UpdateAsync(Guid exposureCaseId, Guid? assignedToUserId, string exposureCategory, string caseStatus,
                                  byte[] rowVersion, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseEdit);
        await cases.UpdateCaseAsync(exposureCaseId, assignedToUserId, exposureCategory, caseStatus, rowVersion, ct).ConfigureAwait(false);
        await audit.ChangedAsync("exposureCase", exposureCaseId, ct).ConfigureAwait(false);
    }

    /// <summary>Staff can override a classification with a required reason; the computed value is kept beside it.</summary>
    public async Task OverrideAsync(Guid exposureCaseId, string classification, string population, string? reason,
                                    byte[] rowVersion, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseEdit);
        new Validator().Required("Reason", reason, "Reason")
            .When(classification is not ("confirmed" or "suspect" or "notACase"), "Classification", "Choose a classification.")
            .When(population is not ("child" or "adult" or "pregnant"), "Population", "Choose a population.")
            .ThrowIfInvalid();
        await cases.OverrideClassificationAsync(exposureCaseId, classification, population, reason!.Trim(), rowVersion, ct).ConfigureAwait(false);
        await audit.ChangedAsync("exposureCase", exposureCaseId, ct).ConfigureAwait(false);
    }

    /// <summary>Logs an activity; after the configured number of failed contact attempts a letter task is queued.</summary>
    public async Task<string?> LogActivityAsync(ActivityInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseEdit);
        new Validator().Required("ActivityType", input.ActivityType, "Activity type")
            .When(input.ActivityDate > clock.Today, "ActivityDate", "The date cannot be in the future.")
            .MaxLength("Note", input.Note, 2000, "Note")
            .ThrowIfInvalid();
        int failed = await cases.SaveActivityAsync(TimeOrderedId.New(), input.ExposureCaseId, input.ActivityType, input.ActivityDate,
            input.ContactPersonId, input.Outcome, input.Note, ct).ConfigureAwait(false);
        await audit.ChangedAsync("exposureCase", input.ExposureCaseId, ct).ConfigureAwait(false);

        var config = await settings.GetAsync(ct).ConfigureAwait(false);
        if (input.ActivityType == "contactAttempt" && config.ContactAttemptsBeforeLetter is int limit && failed >= limit
            && input.Outcome is "noAnswer" or "leftMessage" or "wrongNumber")
        {
            await work.CreateTaskAsync(new WorkTaskCreate(TimeOrderedId.New(), "letterDue", "Send a letter",
                $"{failed} contact attempts have not reached anyone (the jurisdiction sends a letter after {limit}).", null,
                input.ExposureCaseId, null, null, null, false, clock.Today.AddDays(7)), ct).ConfigureAwait(false);
            return "A letter task was queued.";
        }

        return null;
    }

    public async Task CloseAsync(Guid exposureCaseId, string? closureReason, DateOnly? closedDate, string? note, byte[] rowVersion,
                                 CancellationToken ct = default)
    {
        session.Require(Permissions.CaseEdit);
        new Validator().When(closureReason is null || !ClosureReasons.Contains(closureReason), "ClosureReason", "Choose a closure reason.")
            .Required("ClosedDate", closedDate, "Closed date")
            .When(closedDate > clock.Today, "ClosedDate", "The date cannot be in the future.")
            .ThrowIfInvalid();
        await cases.CloseCaseAsync(exposureCaseId, closureReason!, closedDate!.Value, note, rowVersion, ct).ConfigureAwait(false);
        await audit.ChangedAsync("exposureCase", exposureCaseId, ct).ConfigureAwait(false);
    }

    /// <summary>Before creating an investigation the system offers any open investigation of the same target.</summary>
    public async Task<IReadOnlyList<OpenInvestigationRow>> FindOpenInvestigationsAsync(string type, Guid? locationId, Guid? organizationId,
                                                                                       Guid? worksiteLocationId, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        return await cases.FindOpenInvestigationsAsync(type, locationId, organizationId, worksiteLocationId, null, ct).ConfigureAwait(false);
    }

    public async Task<Guid> RequestInvestigationAsync(InvestigationRequest request, CancellationToken ct = default)
    {
        session.Require(Permissions.InvestigationEdit);
        if (request.JoinInvestigationId is Guid existing)
        {
            await cases.LinkInvestigationAsync(existing, request.ExposureCaseId, request.EmploymentId, ct).ConfigureAwait(false);
            await audit.ChangedAsync("investigation", existing, ct).ConfigureAwait(false);
            return existing;
        }

        var v = new Validator();
        v.When(request.InvestigationType is "residential" or "childCareSchool" && request.LocationId is null, "LocationId", "Choose the place.");
        v.When(request.InvestigationType == "workplace" && request.OrganizationId is null, "OrganizationId", "Choose the employer.");
        v.ThrowIfInvalid();

        Guid id = TimeOrderedId.New();
        await cases.CreateInvestigationAsync(new InvestigationCreate(id, request.InvestigationType,
            request.InvestigationType == "workplace" ? null : request.LocationId,
            request.InvestigationType == "workplace" ? request.OrganizationId : null,
            request.InvestigationType == "workplace" ? request.WorksiteLocationId : null, null, request.ExposureCaseId,
            request.EmploymentId, clock.Today), ct).ConfigureAwait(false);
        await audit.CreatedAsync("investigation", id, ct).ConfigureAwait(false);
        return id;
    }
}

/// <summary>Investigations: one target each, possibly serving several cases, with environmental samples.</summary>
public sealed class InvestigationService(IClock clock, ICaseData cases, AuditService audit, ICurrentSession session)
{
    private static readonly string[] Statuses = ["requested", "scheduled", "inProgress", "findingsIssued", "remediation", "cleared", "closed"];

    public async Task<InvestigationDetail> GetAsync(Guid investigationId, CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        var detail = await cases.GetInvestigationAsync(investigationId, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        await audit.ViewedAsync("investigation", investigationId, ct).ConfigureAwait(false);
        return detail;
    }

    public async Task<IReadOnlyList<InvestigationListRow>> ListAsync(bool openOnly, bool assignedToMe, int pageNumber,
                                                                    CancellationToken ct = default)
    {
        session.Require(Permissions.CaseView);
        return await cases.ListInvestigationsAsync(openOnly, assignedToMe, pageNumber, 25, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(InvestigationUpdate update, CancellationToken ct = default)
    {
        session.Require(Permissions.InvestigationEdit);
        var v = new Validator();
        v.When(!Statuses.Contains(update.InvestigationStatus), "InvestigationStatus", "Choose a status.");
        v.When(update.CompletedDate is not null && update.ScheduledDate is not null && update.CompletedDate < update.ScheduledDate,
            "CompletedDate", "Dates must be in order: requested, scheduled, completed.");
        v.When(update.InvestigationStatus is "cleared" or "closed" && update.CompletedDate is null, "CompletedDate",
            "Enter the completed date before clearing or closing.");
        v.ThrowIfInvalid();
        await cases.SaveInvestigationAsync(update, ct).ConfigureAwait(false);
        await audit.ChangedAsync("investigation", update.InvestigationId, ct).ConfigureAwait(false);
    }

    /// <summary>Saves a sample; exceedance is computed by the database from the standard in force, never typed.</summary>
    public async Task<bool?> AddSampleAsync(EnvironmentalSampleSave sample, CancellationToken ct = default)
    {
        session.Require(Permissions.InvestigationEdit);
        new Validator().Required("SampleType", sample.SampleType, "Sample type").Required("Unit", sample.Unit, "Unit")
            .When(sample.SampleValue < 0, "SampleValue", "The value cannot be negative.")
            .When(sample.SampleDate > clock.Today, "SampleDate", "The date cannot be in the future.")
            .ThrowIfInvalid();
        if (sample.EnvironmentalSampleId == Guid.Empty)
        {
            sample = sample with { EnvironmentalSampleId = TimeOrderedId.New() };
        }

        var exceeds = await cases.SaveEnvironmentalSampleAsync(sample, ct).ConfigureAwait(false);
        await audit.ChangedAsync("investigation", sample.InvestigationId, ct).ConfigureAwait(false);
        return exceeds;
    }
}
