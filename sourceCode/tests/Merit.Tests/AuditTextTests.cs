using Merit.Application.Security;
using Merit.Web.Infrastructure;

namespace Merit.Tests;

/// <summary>Issue #45: the audit log reads in words and links only the records the viewer can open.</summary>
public sealed class AuditTextTests
{
    [Theory]
    [InlineData("signIn", "Signed in")]
    [InlineData("mfaEnrolled", "Set up multi-factor authentication")]
    [InlineData("update", "Changed")]
    [InlineData("somethingNew", "Something new")]           // not in the list: split from camel case
    public void ActionsReadAsWords(string code, string words) => Assert.Equal(words, AuditText.Action(code));

    [Theory]
    [InlineData("labResult", "Lab result")]
    [InlineData("stagingRow", "Laboratory file row")]
    [InlineData("report:testingCounts", "Report: Testing counts")]
    [InlineData("report:caseTimeliness", "Report: Case timeliness")]
    public void RecordTypesReadAsWords(string code, string words) => Assert.Equal(words, AuditText.RecordType(code));

    [Fact]
    public void ARecordIsLinkedOnlyWhenTheViewerCanOpenIt()
    {
        var id = Guid.NewGuid();
        var supervisor = new Session(Permissions.AuditView, Permissions.PersonView, Permissions.CaseView, Permissions.ResultView);
        Assert.Equal($"/people/{id}", AuditText.Link("person", id, supervisor));
        Assert.Equal($"/cases/{id}", AuditText.Link("exposureCase", id, supervisor));
        Assert.Equal($"/results/{id}", AuditText.Link("labResult", id, supervisor));
        Assert.Null(AuditText.Link("appUser", id, supervisor));            // user pages need tenant.administer
        Assert.Null(AuditText.Link("stagingRow", id, supervisor));         // no page of its own
        Assert.Null(AuditText.Link("person", null, supervisor));

        var userAdministrator = new Session(Permissions.AuditView, Permissions.TenantAdminister);
        Assert.Null(AuditText.Link("person", id, userAdministrator));
        Assert.Equal($"/admin/users/{id}", AuditText.Link("appUser", id, userAdministrator));
    }

    private sealed class Session(params string[] permissions) : ICurrentSession
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => null;

        public string? UserName => null;

        public string? DisplayName => null;

        public Guid? ScopeKey => null;

        public Guid? RootTenantId => null;

        public Guid? ActingTenantId => null;

        public string? ActingTenantName => null;

        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(permissions);
    }
}
