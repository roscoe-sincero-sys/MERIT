using System.Globalization;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;
using Merit.Domain.Ids;

namespace Merit.Application.Intake;

public sealed record IntakeSenderRow(Guid OrganizationId,string OrganizationName,string? CliaNumber);
public sealed record IntakeFormatSave(Guid IntakeProfileId,Guid? PreviousProfileId,Guid SenderOrganizationId,string ProfileName,string FileType,
    int HeaderRow,string DateFormats,string ColumnMapJson,string? ValueTranslationsJson,string? ConstantsJson,string? HeaderSignature);

public sealed class IntakeFormatService(IIntakeData data,ICurrentSession session,AuditService audit)
{
    public static readonly IReadOnlyDictionary<string,string> Fields=new Dictionary<string,string>
    {
        ["lastName"]="Patient last name",["firstName"]="Patient first name",["middleName"]="Patient middle name",["dob"]="Patient birth date",["sex"]="Sex",
        ["loinc"]="Test code (LOINC)",["value"]="Result value",["unit"]="Result unit",["sampleType"]="Sample type",["collectedDate"]="Collection date",
        ["reportedDate"]="Reported date",["finalResultDate"]="Final-result date",["accessionNumber"]="Accession number",["resultStatus"]="Result status",
        ["addressLine1"]="Patient street",["addressUnit"]="Patient apartment",["city"]="Patient city",["state"]="Patient state",["zip5"]="Patient ZIP",["countyFips"]="County FIPS",
        ["guardianName"]="Caregiver name",["guardianPhone"]="Caregiver phone",["guardianEmail"]="Caregiver email",["guardianAddressLine1"]="Caregiver street",
        ["guardianCity"]="Caregiver city",["guardianState"]="Caregiver state",["guardianZip5"]="Caregiver ZIP",["labCliaNumber"]="Laboratory CLIA",
        ["orderingProviderNpi"]="Ordering provider NPI",["testMethod"]="Test method",["race"]="Race",["ethnicity"]="Ethnicity",
        ["providerName"]="Ordering provider name",["labName"]="Laboratory name",["phone"]="Patient phone",
    };
    public Task<IReadOnlyList<IntakeProfileRow>> ListAsync(CancellationToken ct) { session.Require(Permissions.IntakeConfigure); return data.ListFormatsAsync(ct); }
    public Task<IReadOnlyList<IntakeSenderRow>> SendersAsync(CancellationToken ct) { session.Require(Permissions.IntakeConfigure); return data.ListIntakeSendersAsync(ct); }
    public async Task SaveAsync(Guid? previous,Guid sender,string name,string type,int headerRow,string? formats,IReadOnlyDictionary<string,string> columns,string? header,CancellationToken ct)
    {
        session.Require(Permissions.IntakeConfigure);
        var v=new Validator(); v.Required("ProfileName",name,"Format name").MaxLength("ProfileName",name,120,"Format name");
        v.When(type is not ("hl7" or "csv" or "tsv" or "pipe"),"FileType","Choose HL7 or a supported separated text file.");
        v.When(headerRow is <1 or >100,"HeaderRow","Choose a header row from 1 to 100.");
        var map=columns.Where(p=>!string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p=>p.Key,p=>p.Value.Trim());
        v.When(map.Keys.Any(k=>!Fields.ContainsKey(k)),"Columns","Choose supported import fields.");
        v.When(map.Values.Any(value=>value.Length>200),"Columns","Column names must be 200 characters or fewer.");
        v.When(map.Values.Distinct(StringComparer.Ordinal).Count()!=map.Count,"Columns","Map each file column to only one field.");
        if(type!="hl7") foreach(var required in new[] {"lastName","dob","loinc","value","collectedDate"})
            v.When(!map.ContainsKey(required),"Columns",$"Map {Fields[required]} to its file column.");
        formats=string.IsNullOrWhiteSpace(formats)?"yyyy-MM-dd":formats.Trim();
        v.MaxLength("DateFormats",formats,200,"Date formats").MaxLength("HeaderSignature",header,2000,"Header sample");
        var datePatterns=formats.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        v.When(datePatterns.Length==0,"DateFormats","Enter at least one date format.");
        try { foreach(var f in datePatterns) _=new DateOnly(2026,1,31).ToString(f,CultureInfo.InvariantCulture); }
        catch(FormatException) { v.When(true,"DateFormats","Use supported date formats, for example yyyy-MM-dd or MM/dd/yyyy, separated by commas."); }
        v.ThrowIfInvalid();
        IntakeProfileRow? old=null;
        if(previous is Guid id) old=(await data.ListFormatsAsync(ct)).FirstOrDefault(p=>p.IntakeProfileId==id && p.IsActive) ?? throw new NotFoundException();
        var row=new IntakeFormatSave(TimeOrderedId.New(),previous,sender,name.Trim(),type,headerRow,formats,type=="hl7"?"{}":JsonSerializer.Serialize(map.ToDictionary(p=>p.Value,p=>p.Key,StringComparer.Ordinal)),old?.ValueTranslationsJson,old?.ConstantsJson,header);
        await data.SaveFormatAsync(row,ct); await audit.CreatedAsync("intakeProfile",row.IntakeProfileId,ct);
    }
}
