using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Merit.Worker;

var builder = WebApplication.CreateBuilder(args);
if (string.Equals(builder.Configuration["Logging:Format"], "Json", StringComparison.OrdinalIgnoreCase))
{
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        o.UseUtcTimestamp = true;
    });
}

builder.Services.AddSingleton<JobSessionHolder>();
builder.Services.AddScoped<ICurrentSession, JobCurrentSession>();
builder.Services.AddMeritApplication();
builder.Services.AddMeritSqlServer(o =>
{
    o.ConnectionString = builder.Configuration.GetConnectionString("Merit")
                         ?? throw new InvalidOperationException("ConnectionStrings:Merit is not configured.");
});

// The worker never verifies passwords or protects secrets, but the account service is registered with the rest of the
// application layer, so its dependencies need an implementation.
builder.Services.AddSingleton<IPasswordHasher, UnusedPasswordHasher>();
builder.Services.AddSingleton<ISecretProtector, UnusedSecretProtector>();
builder.Services.AddSingleton<IBreachedPasswordList, UnusedBreachedList>();

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<ClusterScheduler>();
// Sign-in events lose their personal data after SignInLog:RetentionDays (#191).
builder.Services.AddHostedService<SignInLogPurger>();
builder.Services.AddHostedService<ScreenVisitPurger>();
builder.Services.AddHostedService<AuditEventRecordPurger>();
builder.Services.AddHostedService<AuditEventChangePurger>();
builder.Services.AddHostedService<SearchLogPurger>();
builder.Services.AddSingleton<ISecurityAlertNotifier, SecurityAlertWebhookNotifier>();
builder.Services.AddHostedService<ExecutionLogPurger>();
builder.Services.AddHostedService<AuditLedgerVerifier>();
builder.Services.AddHostedService<LedgerAlertOutboxWorker>();
builder.Services.AddHostedService<SecurityFindingScanner>();
builder.Services.AddHostedService<OperationalIntegrityMonitor>();
builder.Services.AddHealthChecks().AddCheck<QueueHealthCheck>("queues");

var app = builder.Build();

// Health: liveness, and queue depth with the oldest waiting job per tenant (deployment rule 8).
app.MapHealthChecks("/health");
app.MapGet("/health/queues", async (IServiceProvider sp, CancellationToken ct) =>
{
    using var scope = sp.CreateScope();
    var depth = await scope.ServiceProvider.GetRequiredService<IWorkData>().GetQueueDepthAsync(ct);
    var summary = depth.GroupBy(d => d.JobType).Select(g => new
    {
        JobType = g.Key,
        Queued = g.Sum(x => x.Queued),
        Running = g.Sum(x => x.Running),
        Failed = g.Sum(x => x.Failed),
        OldestWaitingUtc = g.Min(x => x.OldestWaitingUtc),
    });
    return Results.Ok(summary);
});

app.Run();
