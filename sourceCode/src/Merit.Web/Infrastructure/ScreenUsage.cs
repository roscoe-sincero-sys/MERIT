using Merit.Application.Data;
using Merit.Application.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Merit.Web.Infrastructure;

/// <summary>Usage is best effort and must never prevent the action it describes. Log no request or exception text.</summary>
public sealed partial class ScreenUsageRecorder(IScreenUsageData data, ICurrentSession session, IConfiguration configuration,
                                                ILogger<ScreenUsageRecorder> logger)
{
    public bool Enabled => configuration.GetValue("ScreenVisit:Enabled", true);
    public async Task ContinueAsync(Guid newScope, CancellationToken ct = default)
    {
        if (!Enabled) return;
        try { await data.ContinueSessionAsync(newScope, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Failed(logger, ex.GetType().Name); }
    }
    public async Task<bool> RecordAsync(ScreenVisitInput visit, CancellationToken ct = default)
    {
        if (!Enabled || ScreenCatalog.Find(visit.ScreenCode)?.Allowed(session) != true) return false;
        try { return await data.RecordAsync(visit, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Failed(logger, ex.GetType().Name); }
        return false;
    }

    public async Task DurationAsync(Guid id, int milliseconds, CancellationToken ct = default)
    {
        if (!Enabled || !session.IsAuthenticated) return;
        try { await data.DurationAsync(id, Math.Clamp(milliseconds, 0, 1_800_000), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Failed(logger, ex.GetType().Name); }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen usage recording failed ({ExceptionType}); the page remains available")]
    private static partial void Failed(ILogger logger, string exceptionType);
}

/// <summary>Successful full Razor views, including empty pages. APIs, fragments, assets, errors and Angular hosts are excluded.</summary>
public sealed class ScreenVisitFilter(ScreenUsageRecorder recorder, ICurrentSession session) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var http = context.HttpContext;
        var action = context.ActionDescriptor as ControllerActionDescriptor;
        UsageScreen? screen = null;
        bool saved = action?.ControllerName == "Results" && action.ActionName == "Save"
                     && context.Result is PartialViewResult { ViewName: "_SaveSummary" } or ViewResult { ViewName: "Saved" };
        if (saved) screen = ScreenCatalog.Find("result.saved");
        else if (HttpMethods.IsGet(http.Request.Method) && !http.Request.IsHtmx()
                 && (context.Result is ViewResult || (action?.ControllerName == "Help" && context.Result is PhysicalFileResult)))
            screen = action is null ? null : ScreenCatalog.ForAction(action.ControllerName, action.ActionName,
                http.Request.RouteValues["file"]?.ToString(), isNew: !http.Request.RouteValues.ContainsKey("id"));

        Guid id = Guid.NewGuid();
        if (recorder.Enabled && screen?.Allowed(session) == true)
        {
            var data = context.Result switch { ViewResult v => v.ViewData, PartialViewResult p => p.ViewData, _ => null };
            if (data is not null)
            {
                data["ScreenVisitId"] = id;
                data["ScreenCode"] = screen.Code;
            }
        }
        var executed = await next().ConfigureAwait(false);
        if (screen is not null && executed.Exception is null && !executed.Canceled
            && (http.Response.StatusCode is >= 200 and < 300 || http.Response.StatusCode == 304))
            await recorder.RecordAsync(new ScreenVisitInput(id, screen.Code), http.RequestAborted).ConfigureAwait(false);
    }
}
