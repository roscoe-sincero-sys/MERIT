using Merit.Application.Errors;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Merit.Web.Infrastructure;

/// <summary>
/// USWDS field errors from model state (issue #46): the group's error class, the message (rendered only when there is
/// one, so an empty message takes no space beside a valid field), and the aria attributes that tie it to its input. Razor
/// leaves out an attribute whose value is null, so a valid field gets none. Forms that use these are novalidate: the
/// server's messages are the only ones, in the summary at the top and beside each field.
/// </summary>
public static class FormErrors
{
    /// <summary>The USWDS required marker, explained once at the top of the form by <see cref="RequiredNote"/>.</summary>
    public static readonly IHtmlContent RequiredMark = new HtmlString("<abbr title=\"required\" class=\"usa-hint usa-hint--required\">*</abbr>");

    public static readonly IHtmlContent RequiredNote =
        new HtmlString("<p class=\"usa-hint\">Fields marked with an asterisk (<abbr title=\"required\" class=\"usa-hint usa-hint--required\">*</abbr>) are required.</p>");

    public static void AddErrors(this ModelStateDictionary state, ValidationException ex)
    {
        foreach (var (field, messages) in ex.Errors)
        {
            foreach (var m in messages)
            {
                state.AddModelError(field, m);
            }
        }
    }

    public static bool HasError(this ModelStateDictionary state, string key) => state.FirstError(key).Length > 0;

    public static string GroupClass(this ModelStateDictionary state, string key)
        => state.HasError(key) ? "usa-form-group usa-form-group--error" : "usa-form-group";

    /// <summary>The field's message, with the id <c>{id}-error</c>; nothing when the field is valid.</summary>
    public static IHtmlContent ErrorMessage(this ModelStateDictionary state, string key, string? id = null)
    {
        string message = state.FirstError(key);
        if (message.Length == 0)
        {
            return HtmlString.Empty;
        }

        var html = new HtmlContentBuilder();
        html.AppendHtml($"<span class=\"usa-error-message\" id=\"{id ?? key}-error\">");
        html.Append(message);
        html.AppendHtml("</span>");
        return html;
    }

    /// <summary>The input's aria-describedby: its hint, then its error when there is one.</summary>
    public static string? DescribedBy(this ModelStateDictionary state, string key, string? hintId = null, string? id = null)
    {
        string? error = state.HasError(key) ? $"{id ?? key}-error" : null;
        string joined = string.Join(' ', new[] { hintId, error }.Where(p => p is not null));
        return joined.Length == 0 ? null : joined;
    }

    public static string? AriaInvalid(this ModelStateDictionary state, string key) => state.HasError(key) ? "true" : null;
}
