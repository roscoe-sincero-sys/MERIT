using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Merit.Web.Infrastructure;

/// <summary>Apply the shared server validation presentation to handwritten and MVC-bound fields alike.</summary>
[HtmlTargetElement("input")]
[HtmlTargetElement("select")]
[HtmlTargetElement("textarea")]
public sealed class ValidationFieldTagHelper : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (output.Attributes["type"]?.Value?.ToString() is "hidden" or "submit" or "button") return;
        var name = output.Attributes["name"]?.Value?.ToString();
        var id = output.Attributes["id"]?.Value?.ToString();
        if (name is null || id is null || !ViewContext.ViewData.ModelState.HasError(name)) return;
        var state = ViewContext.ViewData.ModelState;
        output.Attributes.SetAttribute("aria-invalid", "true");
        var described = output.Attributes["aria-describedby"]?.Value?.ToString();
        output.Attributes.SetAttribute("aria-describedby", string.Join(" ", ((described ?? "") + " " + id + "-error").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct()));
        // Forms already using FormErrors render their own field message.
        if (!context.AllAttributes.ContainsName("aria-invalid")) output.PostElement.AppendHtml(state.ErrorMessage(name, id));
    }
}

[HtmlTargetElement("div", Attributes = "class")]
public sealed class ValidationGroupTagHelper : TagHelper
{
    public override int Order => 1100;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var classes = output.Attributes["class"]?.Value?.ToString() ?? "";
        if (!classes.Split(' ').Contains("usa-form-group")) return;
        var child = await output.GetChildContentAsync();
        var html = child.GetContent();
        if (html.Contains("aria-invalid=\"true\"", StringComparison.Ordinal) && !classes.Contains("usa-form-group--error", StringComparison.Ordinal))
            output.Attributes.SetAttribute("class", classes + " usa-form-group--error");
        if (Regex.IsMatch(html, @"\srequired(?:\s|=|/?>)") && !html.Contains("usa-hint--required", StringComparison.Ordinal))
            html = Regex.Replace(html, "</label>", " <abbr aria-hidden=\"true\" title=\"required\" class=\"usa-hint usa-hint--required\">*</abbr></label>", RegexOptions.None, TimeSpan.FromSeconds(1));
        output.Content.SetHtmlContent(html);
    }
}

[HtmlTargetElement("form")]
public sealed class ValidationFormTagHelper : TagHelper
{
    public override int Order => 1200;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("hx-post") && !string.Equals(output.Attributes["method"]?.Value?.ToString(), "post", StringComparison.OrdinalIgnoreCase)) return;
        if (!output.Attributes.ContainsName("novalidate")) output.Attributes.SetAttribute("novalidate", "");
        var child = await output.GetChildContentAsync();
        var html = child.GetContent();
        if (html.Contains("usa-hint--required", StringComparison.Ordinal) && !html.Contains("Fields marked with an asterisk", StringComparison.Ordinal))
            output.PreContent.AppendHtml(FormErrors.RequiredNote);
    }
}
