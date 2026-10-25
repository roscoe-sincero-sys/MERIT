using System.Reflection;
using System.Text;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Merit.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NetArchTest.Rules;

namespace Merit.Tests;

public sealed class TotpTests
{
    // RFC 6238 appendix B: SHA-1 secret "12345678901234567890"; the published 8-digit codes end in these 6 digits.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void MatchesTheRfcTestVectors(long unixSeconds, string code) => Assert.Equal(code, Totp.Code(RfcSecret, unixSeconds / 30));

    [Fact]
    public void AcceptsOneStepOfDriftAndNoMore()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        long step = now.ToUnixTimeSeconds() / 30;
        Assert.True(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 1), now));
        Assert.True(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step + 1), now));
        Assert.False(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 2), now));
        Assert.True(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step).Insert(3, " "), now));
        Assert.False(Totp.Verify(RfcSecret, null, now));
    }

    [Fact]
    public void Base32RoundTrips()
    {
        byte[] secret = Totp.NewSecret();
        Assert.Equal(secret, Totp.FromBase32(Totp.ToBase32(secret)));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.ToBase32(RfcSecret));
    }
}

/// <summary>The layering rules from the architecture requirements, checked against the compiled assemblies.</summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(TimeOrderedId).Assembly;
    private static readonly Assembly Application = typeof(Permissions).Assembly;
    private static readonly Assembly Web = typeof(Merit.Web.Infrastructure.AngularAssets).Assembly;

    private static void AssertNone(TestResult result, string rule)
        => Assert.True(result.IsSuccessful, rule + ": " + string.Join(", ", result.FailingTypeNames ?? []));

    [Fact]
    public void DomainDependsOnNothingElseInMerit()
        => AssertNone(Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny("Merit.Application", "Merit.Data", "Merit.Web", "Merit.Worker", "Microsoft.AspNetCore", "Dapper",
                "Microsoft.Data.SqlClient").GetResult(), "Domain is pure");

    [Fact]
    public void ApplicationDoesNotKnowTheDatabaseOrTheWeb()
        => AssertNone(Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny("Merit.Data", "Merit.Web", "Merit.Worker", "Microsoft.AspNetCore", "Dapper", "Microsoft.Data.SqlClient")
            .GetResult(), "Application is persistence- and UI-agnostic");

    [Fact]
    public void WebNeverTalksToSqlDirectly()
        => AssertNone(Types.InAssembly(Web).ShouldNot().HaveDependencyOnAny("Dapper", "Microsoft.Data.SqlClient").GetResult(),
            "Web goes through application services");

    [Fact]
    public void ControllersDoNotUseDataAccessInterfaces()
    {
        // The I*Data interfaces are the data access boundary; their records (DTOs) may flow up, the interfaces may not.
        string[] dataInterfaces = Application.GetTypes()
            .Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Name.EndsWith("Data", StringComparison.Ordinal))
            .Select(t => t.FullName!).ToArray();
        Assert.NotEmpty(dataInterfaces);
        AssertNone(Types.InAssembly(Web).That().Inherit(typeof(ControllerBase)).ShouldNot()
            .HaveDependencyOnAny(dataInterfaces).GetResult(), "Controllers call services, not data access");
    }
}

/// <summary>
/// Endpoint inventory: every action states its authorization (a permission policy, or an explicit AllowAnonymous on the
/// short list of sign-in pages), and every policy it names is a real permission. Adding an endpoint without deciding
/// who may call it fails the build's tests.
/// </summary>
public sealed class EndpointInventoryTests
{
    private static readonly string[] AllowedAnonymous =
    [
        "AccountController.SignIn", "AccountController.Denied", "HomeController.Error",
    ];

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions()
        => typeof(Merit.Web.Infrastructure.AngularAssets).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
                .Select(m => (t, m)));

    [Fact]
    public void ThereAreEndpoints() => Assert.True(Actions().Count() > 50);

    [Fact]
    public void EveryActionHasAnHttpMethod()
    {
        var missing = Actions().Where(a => !a.Action.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(a => $"{a.Controller.Name}.{a.Action.Name}").ToList();
        Assert.True(missing.Count == 0, "No HTTP method attribute: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryActionNamesAPermissionPolicyOrIsAnApprovedAnonymousPage()
    {
        var problems = new List<string>();
        foreach (var (controller, action) in Actions())
        {
            string name = $"{controller.Name}.{action.Name}";
            bool anonymous = action.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                             || controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var policies = action.GetCustomAttributes<AuthorizeAttribute>().Concat(controller.GetCustomAttributes<AuthorizeAttribute>())
                .Select(a => a.Policy).ToList();

            if (anonymous)
            {
                if (!AllowedAnonymous.Contains(name))
                {
                    problems.Add(name + " allows anonymous access but is not on the approved list");
                }

                continue;
            }

            // The MFA prompt and enrollment pages run on the pending-MFA cookie, before any permission exists.
            var authorize = action.GetCustomAttributes<AuthorizeAttribute>().ToList();
            if (authorize.Count > 0 && authorize.All(a => a.AuthenticationSchemes == AuthSchemes.PendingMfa && a.Policy is null))
            {
                continue;
            }

            if (policies.Count == 0)
            {
                problems.Add(name + " has no [Authorize(Policy = ...)]");
            }

            problems.AddRange(policies.Where(p => p is null || (p != Permissions.SignedIn && !Permissions.All.Contains(p)))
                .Select(p => $"{name} names unknown policy '{p}'"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
