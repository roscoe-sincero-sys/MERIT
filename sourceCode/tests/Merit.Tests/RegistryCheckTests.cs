using System.Reflection;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Application.Services;

namespace Merit.Tests;

/// <summary>Issue #11: a registry file names each organization once, whether a row calls it a laboratory or a facility.</summary>
public sealed class RegistryCheckTests
{
    private static RegistryService Service()
        => new(DispatchProxy.Create<IPlaceData, Unused>(), new RegistryEditor());

    [Fact]
    public void TheSameCliaNumberAsALaboratoryAndAFacilityIsLoadedOnce()
    {
        var check = Service().CheckFile("""
            type,name,lastName,firstName,credential,npi,clia,phone
            laboratory,Chesapeake Reference Laboratory,,,,,21D0123456,
            facility,Chesapeake Reference Laboratory Clinic,,,,,21D-0123456,
            """);

        Assert.Single(check.Ready);
        var problem = Assert.Single(check.Rejected);
        Assert.Equal(3, problem.Line);
        Assert.Contains("Same CLIA number as line 2", problem.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnNpiSharedBetweenALaboratoryAndAFacilityIsLoadedOnce()
    {
        // Line 2 has CLIA X and NPI Y, line 3 has NPI Y: one organization.
        var check = Service().CheckFile("""
            type,name,lastName,firstName,credential,npi,clia,phone
            laboratory,Harbor Labs,,,,1234567893,21D0123456,
            facility,Harbor Pediatrics,,,,1234567893,,
            """);

        Assert.Single(check.Ready);
        Assert.Contains("Same NPI as line 2", Assert.Single(check.Rejected).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderAndAFacilityAreSeparateRegistries()
    {
        // Providers are not organizations, so their NPIs are checked among providers only.
        var check = Service().CheckFile("""
            type,name,lastName,firstName,credential,npi,clia,phone
            facility,Harbor Pediatrics,,,,1234567893,,
            provider,,Rivera,Ana,MD,1245319599,,
            provider,,Rivera,Ana,MD,1245319599,,
            """);

        Assert.Equal(2, check.Ready.Count);
        Assert.Contains("Same NPI as line 3", Assert.Single(check.Rejected).Problem, StringComparison.Ordinal);
    }

    /// <summary>CheckFile never reads the database.</summary>
    public class Unused : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class RegistryEditor : ICurrentSession
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => Guid.NewGuid();

        public string? UserName => "registry";

        public string? DisplayName => "Registry editor";

        public Guid? ScopeKey => null;

        public Guid? RootTenantId => null;

        public Guid? ActingTenantId => null;

        public string? ActingTenantName => null;

        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { Application.Security.Permissions.RegistryEdit };
    }
}
