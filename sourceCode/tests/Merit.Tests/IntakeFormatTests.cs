using System.Reflection;
using System.Text.Json;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Intake;
using Merit.Application.Security;
using Merit.Application.Services;

namespace Merit.Tests;

public sealed class IntakeFormatTests
{
    private static readonly Dictionary<string,string> Columns=new() {["lastName"]="Surname",["dob"]="Birth Date",["loinc"]="Test",["value"]="Reading",["collectedDate"]="Drawn"};
    [Fact]
    public async Task FriendlyFieldFormCreatesParserCompatibleColumnMap()
    {
        var data=DispatchProxy.Create<IIntakeData,Store>(); var store=(Store)(object)data;
        var service=Service(data);
        await service.SaveAsync(null,Guid.NewGuid(),"Clinic CSV","csv",1,"MM/dd/yyyy",Columns,null,default);
        var map=JsonSerializer.Deserialize<Dictionary<string,string>>(store.Saved!.ColumnMapJson)!;
        var fields=IntakeProcessor.StageFields(["Surname","Reading"],["Example","6"],map,new Dictionary<string,Dictionary<string,string>>(),new Dictionary<string,string>());
        Assert.Equal("Example",fields["lastName"]); Assert.Equal("6",fields["value"]);
    }
    [Fact]
    public async Task RejectsAmbiguousAndMissingMappingsBeforeWriting()
    {
        var data=DispatchProxy.Create<IIntakeData,Store>();
        var columns=new Dictionary<string,string>(Columns) {["value"]="Surname"};
        await Assert.ThrowsAsync<ValidationException>(()=>Service(data).SaveAsync(null,Guid.NewGuid(),"Bad","csv",1,null,columns,null,default));
        await Assert.ThrowsAsync<ValidationException>(()=>Service(data).SaveAsync(null,Guid.NewGuid(),"Bad","csv",1,null,new Dictionary<string,string>(),null,default));
        Assert.Null(((Store)(object)data).Saved);
    }
    [Theory]
    [InlineData(",,")]
    [InlineData("HH:mm")]
    public async Task RejectsEmptyOrTimeOnlyDatePatterns(string pattern)
    {
        var data=DispatchProxy.Create<IIntakeData,Store>();
        await Assert.ThrowsAsync<ValidationException>(()=>Service(data).SaveAsync(null,Guid.NewGuid(),"Bad dates","csv",1,pattern,Columns,null,default));
        Assert.Null(((Store)(object)data).Saved);
    }
    private static IntakeFormatService Service(IIntakeData data)=>new(data,new ScreenUsageTests.Actor(new HashSet<string> {Permissions.IntakeConfigure}),new AuditService(DispatchProxy.Create<IWorkData,Audit>()));
    public class Store : DispatchProxy
    {
        public IntakeFormatSave? Saved {get;private set;}
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            Assert.Equal(nameof(IIntakeData.SaveFormatAsync),method?.Name); Saved=(IntakeFormatSave)args![0]!; return Task.CompletedTask;
        }
    }
    public class Audit : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>Task.CompletedTask;
    }
}
