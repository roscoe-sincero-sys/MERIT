using Merit.Application.Data;
using Merit.Application.Errors;

namespace Merit.Application.Jobs;

/// <summary>
/// Opens the session a background job runs under: the same user, profile, view-as and acting tenant as the session
/// that queued it. Without them the job ran under the user's master profile at the highest tenant in it, so a smaller
/// profile did not limit background work, and a region user's file for one state was processed in the region's data
/// root (issue #7).
/// </summary>
public static class JobSessions
{
    public static async Task<SessionScopeResult> OpenAsync(IAccountData accounts, ClaimedJobRow job, DateTime expiresUtc,
                                                           CancellationToken ct = default)
    {
        // A profile that was deactivated, or a view-as the administrator may no longer use, is refused here with its
        // own message, which the worker records on the failed job.
        var scope = await accounts.CreateSessionScopeAsync(Guid.NewGuid(), job.RequestedByUserId, expiresUtc, job.RequestedProfileId,
            job.RequestedViewAsUserId, job.RequestedActingTenantId, ct).ConfigureAwait(false);

        // The scope falls back to another tenant when the requester has lost the one they were working in; running the
        // job there would put its records in the wrong place.
        if (job.RequestedActingTenantId is Guid acting && scope.Summary.ActingTenantId != acting)
        {
            await accounts.RevokeSessionScopeAsync(scope.Summary.SessionScopeId, ct).ConfigureAwait(false);
            throw new BusinessRuleException("The requester no longer has access to the tenant this job was started in, so it was not run.");
        }

        return scope;
    }
}
