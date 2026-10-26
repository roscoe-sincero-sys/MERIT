using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace Merit.Web.Infrastructure;

/// <summary>Display helpers for file names, which have no spaces and would otherwise only break mid-word or not at all.</summary>
public static partial class FileNames
{
    /// <summary>The name, HTML-encoded, with a line-break opportunity after each underscore, dot and hyphen (issues #16, #17).</summary>
    public static IHtmlContent Breakable(string? fileName)
        => new HtmlString(string.Join("<wbr>", Breaks().Split(fileName ?? string.Empty).Select(HtmlEncoder.Default.Encode)));

    [GeneratedRegex("(?<=[_.-])")]
    private static partial Regex Breaks();
}
