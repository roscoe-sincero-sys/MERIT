using System.Reflection;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Application.Services;

namespace Merit.Tests;

public sealed class ContactCorrectionUnitTests
{
    [Theory]
    [InlineData("set", " Apt. 3f ", "260 AUDIT ST|3F|BALTIMORE|MD|21223")]
    [InlineData("remove", null, "260 AUDIT ST||BALTIMORE|MD|21223")]
    [InlineData("unspecified", null, null)]
    public async Task UnitIntentUsesTheResidenceAddressKeyContract(string action, string? unit, string? expectedKey)
    {
        var data = DispatchProxy.Create<IContactCorrectionData, Store>();
        var input = Request(action, unit);
        await Service(data).RequestAsync(input, default);
        var saved = ((Store)(object)data).Saved!;
        Assert.Equal(expectedKey, saved.ProposedAddressKey);
        Assert.Equal(action, saved.UnitAction);
        Assert.Equal(unit?.Trim(), saved.Unit);
        Assert.Equal("MD", saved.State);
    }
    [Theory]
    [InlineData("set", null)]
    [InlineData("set", "APT")]
    [InlineData("set", "1234567890123456789012345678901")]
    [InlineData("remove", "3F")]
    [InlineData("unspecified", "3F")]
    [InlineData("invalid", null)]
    public async Task AmbiguousUnitInstructionsAreRejectedBeforeWriting(string action, string? unit)
    {
        var data = DispatchProxy.Create<IContactCorrectionData, Store>();
        await Assert.ThrowsAsync<ValidationException>(() => Service(data).RequestAsync(Request(action, unit), default));
        Assert.Null(((Store)(object)data).Saved);
    }
    [Fact]
    public async Task PhoneCorrectionCannotCarryAddressUnitInstructions()
    {
        var data = DispatchProxy.Create<IContactCorrectionData, Store>();
        var input = Request("remove", null); input.ContactType = "phone"; input.ProposedValue = "4105550260";
        await Assert.ThrowsAsync<ValidationException>(() => Service(data).RequestAsync(input, default));
        Assert.Null(((Store)(object)data).Saved);
    }
    private static ContactCorrectionInput Request(string action, string? unit) => new()
    {
        PersonId = Guid.NewGuid(), CaregiverId = Guid.NewGuid(), ContactType = "address", ProposedValue = "260 Audit Street",
        City = "Baltimore", State = " md ", Zip5 = "21223", SourceNote = "Verified synthetic address", UnitAction = action, Unit = unit
    };
    private static ContactCorrectionService Service(IContactCorrectionData data)
        => new(data, new ScreenUsageTests.Actor(new HashSet<string> { Permissions.PersonEdit }));
    public class Store : DispatchProxy
    {
        public ContactCorrectionInput? Saved { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IContactCorrectionData.RequestAsync), method?.Name);
            Saved = (ContactCorrectionInput)args![0]!;
            return Task.CompletedTask;
        }
    }
}
