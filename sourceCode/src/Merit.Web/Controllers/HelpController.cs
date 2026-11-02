using System.Text.RegularExpressions;
using Merit.Application.Security;
using Merit.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

/// <summary>
/// The user guides (issue #148). The build copies docs/guides and their images into Help/ beside the application, so the
/// guides people read in MERIT are the ones the PDFs are printed from. Signed-in users only: the screenshots show the
/// demonstration data's screens.
/// </summary>
[Route("help")]
public sealed partial class HelpController : Controller
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Help");

    public static readonly IReadOnlyList<HelpGuide> Guides =
    [
        new("operations-guide.html", "Daily, weekly and quarterly work",
            "Signing in, importing results, held records, match review, people and results, cases, work queues, investigations, reports and extracts."),
        new("npi-clia-import-guide.html", "Importing NPI and CLIA records",
            "Preparing, checking and importing a reporter file, then releasing the results it held."),
        new("access-guide.html", "Who sees what, and your profiles",
            "How MERIT is organized, who can do what, and making and switching profiles."),
    ];

    [HttpGet("")]
    [Authorize(Policy = Permissions.SignedIn)]
    public IActionResult Index() => View(Guides);

    [HttpGet("guides/{file}")]
    [Authorize(Policy = Permissions.SignedIn)]
    public IActionResult Guide(string file)
        => Guides.Any(g => g.File == file) ? StaticFile(Path.Combine(Root, "guides", file), "text/html; charset=utf-8") : NotFound();

    [HttpGet("images/guide/{file}")]
    [Authorize(Policy = Permissions.SignedIn)]
    public IActionResult Image(string file)
        => ImageName().IsMatch(file)
            ? StaticFile(Path.Combine(Root, "images", "guide", file), file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg")
            : NotFound();

    private IActionResult StaticFile(string path, string contentType)
    {
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        // A guide is a static document with its styles inline and no scripts: it may style itself, and run nothing.
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; "
            + "form-action 'none'; frame-ancestors 'none'";
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, contentType);
    }

    [GeneratedRegex("^[a-z0-9-]+\\.(jpg|png)$")]
    private static partial Regex ImageName();
}
