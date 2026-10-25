using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Merit.Worker;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddHealthChecks().AddCheck<QueueHealthCheck>("queues");

var app = builder.Build();

// Health: liveness, and queue depth with the oldest waiting job per tenant (deployment rule 8).
app.MapHealthChecks("/health");
app.MapGet("/health/queues", async (IServiceProvider sp, CancellationToken ct) =>
{
    using var scope = sp.CreateScope();
    var depth = await scope.ServiceProvider.GetRequiredService<IWorkData>().GetQueueDepthAsync(ct);
    return Results.Ok(depth.Select(d => new { d.TenantName, d.JobType, d.Queued, d.Running, d.Failed, d.OldestWaitingUtc }));
});

app.Run();
