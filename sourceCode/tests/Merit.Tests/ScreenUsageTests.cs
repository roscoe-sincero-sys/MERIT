using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Merit.Tests;

public sealed class ScreenUsageTests
{
    [Theory]
    [InlineData("Home", "Index", "home")]
    [InlineData("Intake", "Index", "intake")]
    [InlineData("People", "Details", "person")]
    [InlineData("People", "Results", "person")]
    [InlineData("People", "Edit", "person.edit")]
    [InlineData("Profiles", "Index", "profiles")]
    [InlineData("AppHost", "Index", null)]
    [InlineData("Lookup", "People", null)]
    public void CatalogUsesActionIdentityRatherThanRequestText(string controller, string action, string? code)
        => Assert.Equal(code, ScreenCatalog.ForAction(controller, action)?.Code);

    [Theory]
    [InlineData("Profiles", "Edit", "profile.new", "profile.edit")]
    [InlineData("Admin", "EditUser", "user.new", "user")]
    [InlineData("Config", "EditThreshold", "config.threshold.new", "config.threshold")]
    public void NewAndEditScreensHaveSeparateStaticRoutes(string controller, string action, string create, string edit)
    {
        Assert.Equal(create, ScreenCatalog.ForAction(controller, action, isNew: true)?.Code);
        Assert.Equal(edit, ScreenCatalog.ForAction(controller, action, isNew: false)?.Code);
    }

    [Fact]
    public void CatalogContainsOnlyStaticTemplatesAndEnforcesScreenPermissions()
    {
        Assert.Equal(ScreenCatalog.All.Count, ScreenCatalog.All.Select(s => s.Code).Distinct().Count());
        Assert.All(ScreenCatalog.All, s => { Assert.DoesNotContain('?', s.Route); Assert.DoesNotContain('#', s.Route); });
        Assert.Null(ScreenCatalog.Find("/people/Jane?q=private"));
        Assert.False(ScreenCatalog.Find("usage")!.Allowed(new Actor(new HashSet<string>())));
        Assert.True(ScreenCatalog.Find("reports")!.Allowed(new Actor(new HashSet<string> { Permissions.CaseView })));
        Assert.False(ScreenCatalog.Find("home")!.Allowed(new Actor(Permissions.All.ToHashSet(), false)));
    }

    [Theory]
    [InlineData(false, false, 200, 1)]
    [InlineData(true, false, 200, 0)]
    [InlineData(false, true, 200, 0)]
    [InlineData(false, false, 403, 0)]
    [InlineData(false, false, 500, 0)]
    public async Task FilterRecordsOnlySuccessfulFullScreens(bool htmx, bool partial, int status, int count)
    {
        var data = new MemoryUsage();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/intake/patient-name-that-must-not-be-stored";
        http.Request.QueryString = new QueryString("?q=private-search");
        http.Request.Headers.Referer = "https://example.test/reset?token=secret";
        if (htmx) http.Request.Headers["HX-Request"] = "true";
        http.Response.StatusCode = status;
        IActionResult result = partial ? new PartialViewResult() : new ViewResult();
        await FilterAsync(data, http, "Intake", "Index", result);
        Assert.Equal(count, data.Visits.Count);
        if (count > 0) Assert.Equal("intake", data.Visits[0].ScreenCode);
    }

    [Fact]
    public async Task SavedOutcomeIsRecordedButValidationFragmentsAreNot()
    {
        var data = new MemoryUsage();
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Headers["HX-Request"] = "true";
        await FilterAsync(data, http, "Results", "Save", new PartialViewResult { ViewName = "_SaveSummary" });
        await FilterAsync(data, http, "Results", "Save", new PartialViewResult { ViewName = "_ResultForm" });
        Assert.Equal("result.saved", Assert.Single(data.Visits).ScreenCode);
    }

    [Fact]
    public async Task RecordingFailureAndDisabledCollectionDoNotBreakPages()
    {
        var data = new MemoryUsage { Throw = true };
        var actor = new Actor(Permissions.All.ToHashSet());
        var recorder = new ScreenUsageRecorder(data, actor, new ConfigurationBuilder().Build(), NullLogger<ScreenUsageRecorder>.Instance);
        await recorder.RecordAsync(new ScreenVisitInput(Guid.NewGuid(), "home"));
        await recorder.DurationAsync(Guid.NewGuid(), int.MaxValue);
        await recorder.ContinueAsync(Guid.NewGuid());
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ScreenVisit:Enabled"] = "false" }).Build();
        data.Throw = false;
        await new ScreenUsageRecorder(data, actor, disabled, NullLogger<ScreenUsageRecorder>.Instance).RecordAsync(new ScreenVisitInput(Guid.NewGuid(), "home"));
        Assert.Empty(data.Visits);
    }

    private static async Task FilterAsync(MemoryUsage data, HttpContext http, string controller, string action, IActionResult result)
    {
        var actor = new Actor(Permissions.All.ToHashSet());
        var context = new ActionContext(http, new RouteData(), new ControllerActionDescriptor { ControllerName = controller, ActionName = action });
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());
        if (result is ViewResult v) v.ViewData = viewData;
        if (result is PartialViewResult p) p.ViewData = viewData;
        var executing = new ResultExecutingContext(context, [], result, new object());
        var filter = new ScreenVisitFilter(new ScreenUsageRecorder(data, actor, new ConfigurationBuilder().Build(), NullLogger<ScreenUsageRecorder>.Instance), actor);
        await filter.OnResultExecutionAsync(executing, () => Task.FromResult(new ResultExecutedContext(context, [], result, new object())));
    }

    public sealed class MemoryUsage : IScreenUsageData
    {
        public List<ScreenVisitInput> Visits { get; } = [];
        public bool Throw { get; set; }
        public Task<bool> RecordAsync(ScreenVisitInput v, CancellationToken ct = default) { if (Throw) throw new InvalidOperationException("secret must not be logged"); Visits.Add(v); return Task.FromResult(false); }
        public Task DurationAsync(Guid id, int ms, CancellationToken ct = default) => Throw ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;
        public Task ContinueSessionAsync(Guid id, CancellationToken ct = default) => DurationAsync(id, 0, ct);
        public Task<ScreenUsageReport> ReportAsync(int days, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScreenSessionReport> SessionAsync(Guid reference, int days, int page, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(int days, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public sealed class Actor(IReadOnlySet<string> permissions, bool authenticated = true) : ICurrentSession
    {
        public bool IsAuthenticated => authenticated;
        public Guid? UserId => Guid.Empty;
        public string? UserName => "test";
        public string? DisplayName => "Test";
        public Guid? ScopeKey => Guid.Empty;
        public Guid? RootTenantId => Guid.Empty;
        public Guid? ActingTenantId => Guid.Empty;
        public string? ActingTenantName => "Test";
        public IReadOnlySet<string> Permissions => permissions;
    }
}
