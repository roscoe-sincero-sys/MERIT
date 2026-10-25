using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Extracts;
using Merit.Application.Intake;
using Merit.Application.Reference;

namespace Merit.Application.Jobs;

/// <summary>Background job types. Payloads carry a version number so a job written by one release is readable by the next.</summary>
public static class JobTypes
{
    public const string IntakeBatch = "intakeBatch";
    public const string ExtractRun = "extractRun";
    public const string ClusterDetection = "clusterDetection";
}

public sealed record IntakeBatchPayload(Guid IntakeBatchId, bool ReleasedOnly);

public sealed record ExtractRunPayload(Guid ExtractRunId);

/// <summary>
/// Runs one claimed job through the same application services the controllers use: the worker never carries its own
/// version of a rule. The caller has already set the session for the job's tenant and user.
/// </summary>
public sealed class JobDispatcher(IntakeProcessor intake, ExtractBuilder extracts, IWorkData work, ISettingsProvider settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> RunAsync(ClaimedJobRow job, CancellationToken ct)
    {
        switch (job.JobType)
        {
            case JobTypes.IntakeBatch:
            {
                var p = JsonSerializer.Deserialize<IntakeBatchPayload>(job.PayloadJson, Json)
                        ?? throw new InvalidOperationException("Bad intake payload.");
                var summary = await intake.ProcessAsync(p.IntakeBatchId, p.ReleasedOnly, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(summary, Json);
            }

            case JobTypes.ExtractRun:
            {
                var p = JsonSerializer.Deserialize<ExtractRunPayload>(job.PayloadJson, Json)
                        ?? throw new InvalidOperationException("Bad extract payload.");
                var (records, errors) = await extracts.BuildAsync(p.ExtractRunId, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { records, errors }, Json);
            }

            case JobTypes.ClusterDetection:
            {
                var config = await settings.GetAsync(ct).ConfigureAwait(false);
                if (config.ClusterWindowDays is not int window || config.ClusterMinCount is not int min)
                {
                    return JsonSerializer.Serialize(new { skipped = "Cluster rules are not set for this jurisdiction." }, Json);
                }

                int created = await work.DetectClustersAsync(window, min, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { clusters = created }, Json);
            }

            default:
                throw new InvalidOperationException($"Unknown job type {job.JobType}.");
        }
    }
}
