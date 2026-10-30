using Merit.Application.Services;

namespace Merit.Tests;

public sealed class LookupSelectionTests
{
    [Fact]
    public async Task BlankSelectionDoesNotRequirePermissionOrReadData()
        => Assert.Null(await new LookupService(null!, null!).SelectionAsync("person", null));

    [Fact]
    public async Task BlankResultSelectionsDoNotRequirePermissionOrReadData()
    {
        var selections = await new LookupService(null!, null!).ResultSelectionsAsync(null, null);
        Assert.Null(selections.Laboratory);
        Assert.Null(selections.Provider);
    }
}
