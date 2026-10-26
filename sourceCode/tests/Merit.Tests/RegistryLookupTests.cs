using System.Reflection;
using Merit.Application.Data;
using Merit.Application.Intake;

namespace Merit.Tests;

/// <summary>Issue #14: intake looks each laboratory and ordering provider up once per batch, not once per row.</summary>
public sealed class RegistryLookupTests
{
    private static readonly Guid Lab = Guid.NewGuid();
    private static readonly Guid Provider = Guid.NewGuid();

    [Fact]
    public async Task EachDistinctIdentifierIsLookedUpOncePerBatch()
    {
        var places = FakePlaces.Create();
        var registry = new RegistryLookups(places);

        // A batch of 500 rows from one laboratory, with two ordering providers, one of them unknown.
        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(Lab, await registry.LaboratoryAsync("21D0000001", CancellationToken.None));
            Assert.Equal(i % 2 == 0 ? Provider : null, await registry.ProviderAsync(i % 2 == 0 ? "1234567893" : "1245319599", CancellationToken.None));
        }

        // An unknown laboratory is remembered as unknown too.
        Assert.Null(await registry.LaboratoryAsync("99D9999999", CancellationToken.None));
        Assert.Null(await registry.LaboratoryAsync("99d9999999", CancellationToken.None));

        var calls = FakePlaces.Of(places).Calls;
        Assert.Equal(["clia:21D0000001", "npi:1234567893", "npi:1245319599", "clia:99D9999999"], calls);
    }

    [Fact]
    public async Task ABlankCliaIsNotLookedUp()
    {
        var places = FakePlaces.Create();
        Assert.Null(await new RegistryLookups(places).LaboratoryAsync(null, CancellationToken.None));
        Assert.Empty(FakePlaces.Of(places).Calls);
    }

    /// <summary>IPlaceData with only the two lookups intake makes.</summary>
    public class FakePlaces : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        public static IPlaceData Create() => Create<IPlaceData, FakePlaces>();

        public static FakePlaces Of(IPlaceData proxy) => (FakePlaces)(object)proxy;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(IPlaceData.ResolveOrganizationAsync):
                    string clia = (string)args![0]!;
                    Calls.Add("clia:" + clia);
                    return Task.FromResult(clia == "21D0000001" ? new OrganizationResolveRow { OrganizationId = Lab } : null);
                case nameof(IPlaceData.GetProviderByNpiAsync):
                    string npi = (string)args![0]!;
                    Calls.Add("npi:" + npi);
                    return Task.FromResult(npi == "1234567893" ? new ProviderSearchRow { ProviderId = Provider, Npi = npi } : null);
                default:
                    throw new NotSupportedException(method?.Name);
            }
        }
    }
}
