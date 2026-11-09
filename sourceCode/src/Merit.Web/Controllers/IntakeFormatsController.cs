using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Intake;
using Merit.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Merit.Web.Controllers;

public sealed class IntakeFormatModel
{
    public Guid? PreviousProfileId {get;set;}
    public Guid SenderOrganizationId {get;set;}
    public string ProfileName {get;set;}="";
    public string FileType {get;set;}="hl7";
    public int HeaderRow {get;set;}=1;
    public string DateFormats {get;set;}="yyyy-MM-dd";
    public string? HeaderSignature {get;set;}
    public Dictionary<string,string> Columns {get;set;}=[];
    public IReadOnlyList<IntakeProfileRow> Formats {get;set;}=[];
    public IReadOnlyList<IntakeSenderRow> Senders {get;set;}=[];
}

[Authorize(Policy=Permissions.IntakeConfigure)]
[Route("config/intake-formats")]
public sealed class IntakeFormatsController(IntakeFormatService formats) : Controller
{
    [HttpGet("")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Index(Guid? id,CancellationToken ct)
    {
        var model=new IntakeFormatModel();
        if(id is Guid key)
        {
            var row=(await formats.ListAsync(ct)).FirstOrDefault(r=>r.IntakeProfileId==key && r.IsActive) ?? throw new NotFoundException();
            model=new() {PreviousProfileId=key,SenderOrganizationId=row.SenderOrganizationId,ProfileName=row.ProfileName,FileType=row.FileType,
                HeaderRow=row.HeaderRow,DateFormats=row.DateFormats??"yyyy-MM-dd",HeaderSignature=row.HeaderSignature,
                Columns=(JsonSerializer.Deserialize<Dictionary<string,string>>(row.ColumnMapJson)??[]).GroupBy(p=>p.Value).ToDictionary(g=>g.Key,g=>g.First().Key)};
        }
        return await Page(model,ct);
    }
    [HttpPost("")]
    public async Task<IActionResult> Save(IntakeFormatModel model,CancellationToken ct)
    {
        try
        {
            if(!ModelState.IsValid) { Response.StatusCode=422; return await Page(model,ct); }
            await formats.SaveAsync(model.PreviousProfileId,model.SenderOrganizationId,model.ProfileName,model.FileType,model.HeaderRow,model.DateFormats,model.Columns,model.HeaderSignature,ct);
            TempData["FormatMessage"]="Laboratory format saved. Earlier versions and their batches are retained.";
            return RedirectToAction(nameof(Index));
        }
        catch(ValidationException ex) { foreach(var pair in ex.Errors) foreach(var message in pair.Value) ModelState.AddModelError(pair.Key,message); }
        catch(MeritException ex) when(ex is BusinessRuleException or ConcurrencyException) { ModelState.AddModelError("",ex.Message); }
        Response.StatusCode=422; return await Page(model,ct);
    }
    private async Task<IActionResult> Page(IntakeFormatModel model,CancellationToken ct)
    {
        model.Formats=await formats.ListAsync(ct); model.Senders=await formats.SendersAsync(ct); return View("Index",model);
    }
}
