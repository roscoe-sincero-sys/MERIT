using Merit.Application.Errors;
using Merit.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Merit.Web.Infrastructure;

/// <summary>
/// Shared error handling for both kinds of controller. API actions get RFC 9457 problem details. htmx requests get a
/// USWDS alert partial retargeted at the page's alert region (htmx 4 swaps error responses by default); full-page
/// requests get the error page. Messages come from application exceptions and never carry patient data.
/// </summary>
public sealed partial class MeritExceptionFilter(ILogger<MeritExceptionFilter> logger, IModelMetadataProvider metadata) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not MeritException)
        {
            // Type and stack only: exception messages (SQL errors in particular) can quote data values, and logs carry
            // record ids, never patient data.
            LogUnhandled(logger, context.Exception.GetType().FullName ?? "Exception", context.HttpContext.TraceIdentifier,
                context.Exception.StackTrace ?? string.Empty);
        }

        var (status, title, message) = context.Exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found", e.Message),
            PermissionDeniedException e => (StatusCodes.Status403Forbidden, "Not permitted", e.Message),
            ConcurrencyException e => (StatusCodes.Status409Conflict, "Changed by someone else", e.Message),
            BusinessRuleException e => (StatusCodes.Status422UnprocessableEntity, "Cannot save", e.Message),
            ValidationException e => (StatusCodes.Status422UnprocessableEntity, "Check the form",
                string.Join(" ", e.Errors.SelectMany(kv => kv.Value))),
            _ => (StatusCodes.Status500InternalServerError, "Something went wrong",
                $"The request could not be completed. Reference {context.HttpContext.TraceIdentifier}."),
        };

        var request = context.HttpContext.Request;
        if (context.ActionDescriptor.EndpointMetadata.OfType<ApiControllerAttribute>().Any()
            || request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            var problem = new ProblemDetails { Status = status, Title = title, Detail = message, Instance = request.Path };
            problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            if (context.Exception is ValidationException ve)
            {
                problem.Extensions["errors"] = ve.Errors;
            }

            context.Result = new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        }
        else if (request.IsHtmx())
        {
            context.HttpContext.Response.Headers["HX-Retarget"] = request.Headers["X-Merit-Dialog"] == "true"
                ? "#dialog-alert-region" : "#alert-region";
            context.HttpContext.Response.Headers["HX-Reswap"] = "innerHTML";
            context.Result = new PartialViewResult
            {
                ViewName = "_Alert",
                StatusCode = status,
                ViewData = new ViewDataDictionary<AlertModel>(metadata, context.ModelState)
                {
                    Model = new AlertModel(status >= 500 ? "error" : "warning", title, message),
                },
            };
        }
        else
        {
            context.Result = new ViewResult
            {
                ViewName = "Error",
                StatusCode = status,
                ViewData = new ViewDataDictionary<AlertModel>(metadata, context.ModelState)
                {
                    Model = new AlertModel(status >= 500 ? "error" : "warning", title, message),
                },
            };
        }

        context.ExceptionHandled = true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled {ExceptionType} (trace {TraceId}) at {StackTrace}")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType, string traceId, string stackTrace);
}
