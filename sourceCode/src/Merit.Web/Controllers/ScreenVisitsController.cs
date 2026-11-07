using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Merit.Web.Controllers;

[ApiController]
[Route("api/v1/usage")]
[Authorize(Policy = Permissions.SignedIn)]
[EnableRateLimiting("screenUsage")]
public sealed class ScreenVisitsController(ScreenUsageRecorder recorder, ICurrentSession session) : ControllerBase
{
    private static readonly HashSet<string> Types = ["direct", "menu", "link", "search", "back", "reload", "redirect", "router"];

    [HttpPost("visits")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> Visit(ScreenVisitInput input, CancellationToken ct)
    {
        if (input.VisitId == Guid.Empty || input.TabId is null || input.TabId == Guid.Empty
            || !Types.Contains(input.NavigationType) || ScreenCatalog.Find(input.ScreenCode)?.Allowed(session) != true)
            return BadRequest();
        await recorder.RecordAsync(input, ct);
        return NoContent();
    }

    [HttpPost("duration")]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> Duration([FromForm] Guid visitId, [FromForm] int durationMs, CancellationToken ct)
    {
        if (visitId == Guid.Empty || durationMs is < 0 or > 1_800_000) return BadRequest();
        await recorder.DurationAsync(visitId, durationMs, ct);
        return NoContent();
    }
}
