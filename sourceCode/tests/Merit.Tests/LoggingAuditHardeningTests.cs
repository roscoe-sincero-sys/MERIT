using System.Diagnostics;
using System.Reflection;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Merit.Tests;

/// <summary>
/// Unit and inventory tests for logging & audit hardening (#325, #326, #331, #332, #333).
/// </summary>
public sealed class LoggingAuditHardeningTests
{
    [Fact]
    public async Task AuditServiceDeduplicatesViewsAndPermissionDeniedWithinSingleRequest()
    {
        var work = new RecordingWorkData();
        var ctx = new StubAuditContext("203.0.113.42", "0123456789abcdef0123456789abcdef");
        var audit = new AuditService(work, ctx);
        var personId = Guid.NewGuid();
        var caseId = Guid.NewGuid();

        await audit.ViewedAsync("person", personId);
        await audit.ViewedAsync("person", personId); // duplicate in same request -> collapsed
        await audit.ViewedIncidentalAsync("exposureCase", caseId);
        await audit.PermissionDeniedAsync("auditEvent");
        await audit.PermissionDeniedAsync("auditEvent"); // duplicate in same request -> collapsed

        Assert.Equal(3, work.AuditCalls.Count);
        Assert.Equal(("view", "person", personId, "203.0.113.42", "direct", "0123456789abcdef0123456789abcdef"),
            (work.AuditCalls[0].EventAction, work.AuditCalls[0].EntityType, work.AuditCalls[0].EntityId,
             work.AuditCalls[0].ClientAddress, work.AuditCalls[0].ViewKind, work.AuditCalls[0].TraceId));
        Assert.Equal(("view", "exposureCase", caseId, "incidental"),
            (work.AuditCalls[1].EventAction, work.AuditCalls[1].EntityType, work.AuditCalls[1].EntityId, work.AuditCalls[1].ViewKind));
        Assert.Equal(("permissionDenied", "auditEvent", (Guid?)null),
            (work.AuditCalls[2].EventAction, work.AuditCalls[2].EntityType, work.AuditCalls[2].EntityId));
    }

    [Fact]
    public async Task AuditServiceSerializesFilterAndDeduplicatedManifest()
    {
        var work = new RecordingWorkData();
        var ctx = new StubAuditContext("198.51.100.7", "fedcba9876543210fedcba9876543210");
        var audit = new AuditService(work, ctx);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await audit.ExportedAsync("testingOutreach", null, 2, CancellationToken.None,
            new { dueFilter = "overdue", page = 1 },
            [new AuditManifestEntry("person", id1), new AuditManifestEntry("person", id1), new AuditManifestEntry("person", id2)]);

        var call = Assert.Single(work.AuditCalls);
        Assert.Equal("export", call.EventAction);
        Assert.Equal("198.51.100.7", call.ClientAddress);
        Assert.Equal("fedcba9876543210fedcba9876543210", call.TraceId);
        Assert.Contains("\"dueFilter\":\"overdue\"", call.FilterJson);
        Assert.Contains(id1.ToString(), call.RecordIdsJson);
        Assert.Contains(id2.ToString(), call.RecordIdsJson);
        // Deduplicated manifest should contain exactly 2 entries.
        using var doc = System.Text.Json.JsonDocument.Parse(call.RecordIdsJson!);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void RequestTracePrefersW3CActivityTraceIdAndSanitizesUserAgent()
    {
        var http = new DefaultHttpContext { TraceIdentifier = "aspnetcore-fallback-id" };
        using var activity = new Activity("test-request").Start();
        Assert.Equal(activity.TraceId.ToString(), RequestTrace.Id(http));

        Assert.Equal("Mozilla/5.0  Evil", RequestTrace.SanitizeUserAgent("Mozilla/5.0\r\nEvil"));
        Assert.Equal("Permission denied", AuditText.Action("permissionDenied"));
    }

    [Fact]
    public async Task SecurityTelemetryLogsAntiforgeryAndBrowserAccessDeniedAndSkipsReExecution()
    {
        var entries = new List<string>();
        var logger = new ListLogger<SecurityTelemetryMiddleware>(entries);

        // 1. Antiforgery failure (400 + AntiforgeryFailedItem set by AntiforgeryTelemetryFilter) -> logged once as antiforgery.
        var afMiddleware = new SecurityTelemetryMiddleware(ctx =>
        {
            ctx.Items[SecurityTelemetryMiddleware.AntiforgeryFailedItem] = true;
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }, logger);
        var afHttp = new DefaultHttpContext();
        afHttp.Request.Method = "POST";
        afHttp.Request.Path = "/config/settings/sessionTimeoutMinutes";
        await afMiddleware.InvokeAsync(afHttp);
        Assert.Single(entries);
        Assert.Contains("Security HTTP antiforgery status 400", entries[0]);

        // 2. Browser access denied (302 redirect + AccessDeniedItem set by OnRedirectToAccessDenied) -> logged as forbidden 403.
        entries.Clear();
        var deniedMiddleware = new SecurityTelemetryMiddleware(ctx =>
        {
            ctx.Items[SecurityTelemetryMiddleware.AccessDeniedItem] = true;
            ctx.Response.StatusCode = StatusCodes.Status302Found;
            return Task.CompletedTask;
        }, logger);
        var deniedHttp = new DefaultHttpContext();
        deniedHttp.Request.Method = "GET";
        deniedHttp.Request.Path = "/admin/audit";
        await deniedMiddleware.InvokeAsync(deniedHttp);
        Assert.Single(entries);
        Assert.Contains("Security HTTP forbidden status 403", entries[0]);

        // 3. StatusCodePages re-execution -> skipped so failed requests are never double-logged.
        entries.Clear();
        var reexecMiddleware = new SecurityTelemetryMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }, logger);
        var reexecHttp = new DefaultHttpContext();
        reexecHttp.Features.Set<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>(new FakeReExecuteFeature());
        reexecHttp.Request.Method = "GET";
        reexecHttp.Request.Path = "/home/status/404";
        await reexecMiddleware.InvokeAsync(reexecHttp);
        Assert.Empty(entries);
    }

    /// <summary>
    /// Issue #331 inventory test: reflects over every GET action requiring a patient-data or security-log permission
    /// and fails if any new action lacks audit coverage or an explicit reviewed exemption.
    /// </summary>
    [Fact]
    public void AllPatientDataAndSecurityLogGetActionsAreAuditedOrReviewedExempt()
    {
        var guardedPolicies = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.PersonView,
            Permissions.ResultView,
            Permissions.CaseView,
            Permissions.ResultReleaseHold,
            Permissions.AuditView,
            Permissions.SecurityReview,
            Permissions.SecurityReports,
            Permissions.SecurityInvestigate,
        };

        // Reviewed inventory of GET actions on guarded permissions:
        // Audited (service/controller writes logs.auditEvent) or explicitly exempt (blank forms, static reference, templates).
        var reviewedActions = new HashSet<string>(StringComparer.Ordinal)
        {
            // PeopleController
            "PeopleController.New",            // exempt: blank create form
            "PeopleController.Details",        // audited: PersonService.GetPageAsync
            "PeopleController.Header",         // audited: PersonService.GetHeaderAsync (#331)
            "PeopleController.Edit",           // audited: PersonService.GetHeaderAsync (#331)
            "PeopleController.Results",        // audited: PersonService.GetResultsAsync (#331)
            "PeopleController.Cases",          // audited: PersonService.GetCasesAsync (#331)
            "PeopleController.Relationships",  // audited: PersonService.GetRelationshipsAsync (#331)
            "PeopleController.Employment",     // audited: PersonService.GetEmploymentAsync (#331)
            "PeopleController.Sources",        // audited: PersonService.GetExposureSourcesAsync (#331)
            "PeopleController.Pregnancies",    // audited: PersonService.GetPregnanciesAsync (#331)
            "PeopleController.EditPregnancy",  // audited: PersonService.GetPregnanciesAsync (#331)
            "PeopleController.NewRelationship",// exempt: blank form / header
            "PeopleController.NewEmployment",  // exempt: blank form
            "PeopleController.NewSource",      // exempt: blank form
            "PeopleController.NewPregnancy",   // exempt: blank form
            // PlacesController
            "PlacesController.Index",          // audited: PlaceService.GetPlacesAsync (#331)
            "PlacesController.New",            // exempt: blank form
            "PlacesController.Edit",           // audited: PlaceService.GetPlacesAsync (#331)
            "PlacesController.Lookup",         // exempt: location address type-ahead
            "PlacesController.SetPrimaryForm", // audited: PlaceService.GetPlacesAsync (#331)
            // ResultsController
            "ResultsController.New",           // audited: PersonService.GetHeaderAsync (#331)
            "ResultsController.Details",       // audited: ResultService.GetAsync
            "ResultsController.Correct",       // audited: ResultService.GetAsync
            // CasesController & InvestigationsController
            "CasesController.Index",           // audited: CaseService.ListAsync (#331)
            "CasesController.Details",         // audited: CaseService.GetAsync
            "CasesController.TransferForm",    // audited: CaseService.GetAsync
            "InvestigationsController.Index",  // audited: InvestigationService.ListAsync (#331)
            "InvestigationsController.Details",// audited: InvestigationService.GetAsync
            "InvestigationsController.New",    // exempt: blank form
            // CareController & TestingOutreachController & WorkflowController & ContactCorrectionsController
            "CareController.Index",            // audited: CareService.PageAsync
            "CareController.Download",         // audited: CareService.DocumentAsync (export)
            "CareController.Preview",          // audited: CareService.DocumentAsync (export)
            "CareController.Image",            // audited: CareService.DocumentAsync (export)
            "CareController.ReceiptQuality",   // audited: CareService.ReceiptQualityAsync
            "CareController.Testing",          // audited: CareService.TestingPlansAsync
            "CareController.Notifications",    // audited: CareService.NotificationsAsync
            "CareController.Export",           // audited: CareService.ExportAsync
            "CareController.Policies",         // exempt: notification rule policy definitions (no PHI)
            "TestingOutreachController.Index", // audited: CareService.TestingOutreachAsync
            "TestingOutreachController.Export",// audited: CareService.ExportTestingAsync
            "WorkflowController.Index",        // audited: WorkflowService
            "WorkflowController.PatientWork",  // audited: WorkflowService.PatientWorkAsync
            "ContactCorrectionsController.Index", // audited: ContactCorrectionService
            "ContactCorrectionsController.RequestForm", // audited: PersonService.GetHeaderAsync
            // Search & Lookup & Reporters
            "SearchController.Index",          // audited: dbo.uspSearch -> logs.searchLog (+ personOutsideScope)
            "LookupController.People",         // audited: dbo.uspSearch -> logs.searchLog
            "LookupController.Organizations",  // exempt: reference organization type-ahead
            "LookupController.Sites",          // exempt: reference organization site type-ahead
            "LookupController.Providers",      // exempt: reference provider type-ahead
            "LookupController.Locations",      // exempt: reference location type-ahead
            "ReportersController.Index",       // exempt: reporter registry reference list
            "ReportersController.Template",    // exempt: static CSV template
            "ReportersController.Organization",// exempt: reference organization detail
            // API Controllers
            "ResultsApiController.Analytes",   // exempt: reference analyte options
            "ResultsApiController.List",       // audited: ResultService.ListAsync (#333)
            "ResultsApiController.Timeline",   // audited: PersonService.GetPageAsync + CaseService.GetAsync(incidental) (#333)
            "HeldRecordsController.Groups",    // exempt: aggregate hold reason counts (no PHI)
            "HeldRecordsController.Overview",  // exempt: aggregate hold reason counts (no PHI)
            "HeldRecordsController.Summary",   // exempt: aggregate hold count (no PHI)
            "HeldRecordsController.Rows",      // audited: IntakeService.HeldRowsAsync
            "HeldRecordsController.Row",       // audited: IntakeService.HeldRowAsync
            "MatchReviewController.Queue",     // audited: MatchReviewService.QueueAsync (#331)
            "MatchReviewController.Get",       // audited: MatchReviewService.GetAsync
            "ClustersController.Picker",       // audited: CaseService.ListAsync -> logs.searchLog + logs.auditEvent (#331)
            "ClustersController.List",         // audited: ClusterService.ListAsync
            "ClustersController.Get",          // audited: ClusterService.GetAsync
            "ClustersController.Map",          // audited: ClusterService.MapAsync
            "ReportsApiController.List",       // exempt: static report catalog
            "ReportsApiController.Run",        // audited: ReportService.RunAsync
            "ReportsApiController.Export",     // audited: ReportService.ExportAsync
            // Security log & admin controllers
            "AdminController.Users",           // exempt: user directory (admin/security review)
            "AdminController.UsersCsv",        // audited: AdminController.UsersCsv (export)
            "AdminController.SignInHistoryCsv",// audited: AdminController.SignInHistoryCsv (export)
            "AdminController.Audit",           // audited: AdminController.Audit (#331)
            "AdminController.AuditCsv",        // audited: AdminController.AuditCsv (export)
            "AdminController.AuditRecords",    // audited: AdminController.AuditRecords (#333)
            "SignInActivityController.Index",  // audited: SignInLogService.ActivityAsync (#331)
            "SignInActivityController.ActivityCsv", // audited: SignInLogService.ExportActivityAsync
            "SignInActivityController.UnknownNames", // audited: SignInLogService.UnknownNamesAsync (#331)
            "SignInActivityController.Export", // audited: SignInLogService.ExportUnknownNamesAsync
        };

        var webAssembly = typeof(Merit.Web.Controllers.AdminController).Assembly;
        var unreviewed = new List<string>();

        foreach (var controller in webAssembly.GetTypes().Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)))
        {
            var classPolicy = controller.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).Where(p => p is not null).ToList();
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.GetCustomAttributes<HttpGetAttribute>(true).Any()) continue;
                var policies = classPolicy.Concat(method.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).Where(p => p is not null));
                if (!policies.Any(p => p is not null && guardedPolicies.Contains(p))) continue;

                string key = $"{controller.Name}.{method.Name}";
                if (!reviewedActions.Contains(key))
                {
                    unreviewed.Add(key);
                }
            }
        }

        Assert.True(unreviewed.Count == 0,
            "Unreviewed GET action(s) on patient-data or security-log permissions (add audit coverage or [AuditExempt]): "
            + string.Join(", ", unreviewed));
    }

    /// <summary>
    /// Issue #332 inventory test: verifies key patient-data write stored procedures record in-transaction audit events.
    /// </summary>
    [Fact]
    public void PatientDataWriteProceduresIncludeInTransactionAuditInserts()
    {
        string procsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "database", "scripts", "400_procedures"));
        Assert.True(Directory.Exists(procsDir), $"Expected procedures directory at {procsDir}");

        string[] requiredAuditedProcs =
        [
            "dbo.uspSavePersonLocations.sql",
            "dbo.uspSaveStagingCorrection.sql",
            "dbo.uspCorrectHeldGroup.sql",
            "dbo.uspReleaseStagingRows.sql",
            "dbo.uspRejectStagingRows.sql",
            "dbo.uspOpenExposureCase.sql",
            "dbo.uspAttachResultToCase.sql",
            "dbo.uspSaveLabResult.sql",
        ];

        foreach (string file in requiredAuditedProcs)
        {
            string sql = File.ReadAllText(Path.Combine(procsDir, file));
            Assert.Contains("INSERT logs.auditEvent", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class StubAuditContext(string? clientAddress, string? traceId) : IRequestAuditContext
    {
        public string? ClientAddress => clientAddress;
        public string? TraceId => traceId;
        public string? UserAgent => "UnitTestAgent/1.0";
        public string? RouteTemplate => "/test";
    }

    private sealed class RecordingWorkData : IWorkData
    {
        public List<(string EventAction, string EntityType, Guid? EntityId, int? RecordCount, string? ClientAddress, string? ViewKind, string? FilterJson, string? RecordIdsJson, string? TraceId)> AuditCalls { get; } = [];

        public Task RecordAuditAsync(string eventAction, string entityType, Guid? entityId, int? recordCount, string? clientAddress, CancellationToken ct = default,
                                     string? viewKind = null, string? filterJson = null, string? recordIdsJson = null, string? traceId = null)
        {
            AuditCalls.Add((eventAction, entityType, entityId, recordCount, clientAddress, viewKind, filterJson, recordIdsJson, traceId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEventRecordRow>> GetAuditEventRecordsAsync(long auditEventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditEventRecordRow>>([]);
        public Task<int> PurgeAuditEventRecordsAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordSearchLogAsync(string searchText, int resultCount, string searchSource, string entityType, string? filterJson = null, string? recordIdsJson = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateTaskAsync(WorkTaskCreate task, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<QueueCountRow>> GetQueueCountsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardData> GetDashboardAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkTaskRow>> ListTasksAsync(WorkTaskQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> UpdateTasksAsync(IReadOnlyList<Guid> taskIds, string action, Guid? assignToUserId, string? note, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnqueueJobAsync(Guid jobId, string jobType, string payloadJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ClaimedJobRow?> ClaimNextJobAsync(string workerName, int leaseSeconds = 300, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RenewJobClaimAsync(Guid jobId, Guid claimToken, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CompleteJobAsync(Guid jobId, string? resultJson, Guid? claimToken = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> FailJobAsync(Guid jobId, string errorText, Guid? claimToken = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JobRow?> GetJobAsync(Guid jobId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<QueueDepthRow>> GetQueueDepthAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> DetectClustersAsync(int windowDays, int minCount, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEventRow>> SearchAuditAsync(AuditQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditUserRow>> ListAuditUsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class ListLogger<T>(List<string> entries) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add(formatter(state, exception));
    }

    private sealed class FakeReExecuteFeature : Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature
    {
        public string OriginalPathBase { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = "/missing";
        public string? OriginalQueryString { get; set; }
    }
}
