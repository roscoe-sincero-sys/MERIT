using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Jobs;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Merit.IntegrationTests;

public sealed class Scenario267ReplayFactAttribute : FactAttribute
{
    public Scenario267ReplayFactAttribute()
    {
        if (!TestEnvironment.Database.StartsWith("MERIT_SCENARIO_267_", StringComparison.Ordinal))
            Skip = "Opt-in replay: creates about 600 synthetic accounts. Set MERIT_IT_DATABASE to a dedicated MERIT_SCENARIO_267_* database.";
        else Skip = TestEnvironment.SkipReason;
    }
}

/// <summary>Issue #267 (test scenario 2): EPA Region 3 delegation from the platform down to Maryland and Virginia
/// counties, Maryland policy configuration, two-county intake, three county case/investigation workflows, Virginia
/// isolation from Maryland records, profiles, and the security reports. Every account, tenant, grant, profile and
/// record is created through real HTTP pages; only identifiers are read from SQL. Business dates are real (no clock
/// instrumentation). The replay records observations; gaps found are filed as issues and pinned here.</summary>
[Collection(MeritCollection.Name)]
public sealed class Scenario267RegionTests(MeritFixture fixture, ITestOutputHelper output)
{
    private const string Password = "Scenario267!SyntheticPassphrase";
    private readonly List<MeritClient> clients = [];
    private readonly List<string> evidence = [];
    private readonly List<string> unexpected = [];
    private readonly Dictionary<string, MeritClient> signedIn = new(StringComparer.Ordinal);
    private readonly DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));
    private Guid mdRoot, specimen, analyte, lab;
    private string suffix = "", clia = "";

    private static readonly (string Fips, string Name)[] MarylandCounties =
    [
        ("001","Allegany County"),("003","Anne Arundel County"),("005","Baltimore County"),("009","Calvert County"),("011","Caroline County"),
        ("013","Carroll County"),("015","Cecil County"),("017","Charles County"),("019","Dorchester County"),("021","Frederick County"),
        ("023","Garrett County"),("025","Harford County"),("027","Howard County"),("029","Kent County"),("031","Montgomery County"),
        ("033","Prince George's County"),("035","Queen Anne's County"),("037","St. Mary's County"),("039","Somerset County"),("041","Talbot County"),
        ("043","Washington County"),("045","Wicomico County"),("047","Worcester County"),("510","Baltimore city"),
    ];

    private static readonly (string Fips, string Name)[] VirginiaCounties =
    [
        ("001","Accomack County"),("003","Albemarle County"),("005","Alleghany County"),("007","Amelia County"),("009","Amherst County"),
        ("011","Appomattox County"),("013","Arlington County"),("015","Augusta County"),("017","Bath County"),("019","Bedford County"),
        ("021","Bland County"),("023","Botetourt County"),("025","Brunswick County"),("027","Buchanan County"),("029","Buckingham County"),
        ("031","Campbell County"),("033","Caroline County"),("035","Carroll County"),("036","Charles City County"),("037","Charlotte County"),
        ("041","Chesterfield County"),("043","Clarke County"),("045","Craig County"),("047","Culpeper County"),("049","Cumberland County"),
        ("051","Dickenson County"),("053","Dinwiddie County"),("057","Essex County"),("059","Fairfax County"),("061","Fauquier County"),
        ("063","Floyd County"),("065","Fluvanna County"),("067","Franklin County"),("069","Frederick County"),("071","Giles County"),
        ("073","Gloucester County"),("075","Goochland County"),("077","Grayson County"),("079","Greene County"),("081","Greensville County"),
        ("083","Halifax County"),("085","Hanover County"),("087","Henrico County"),("089","Henry County"),("091","Highland County"),
        ("093","Isle of Wight County"),("095","James City County"),("097","King and Queen County"),("099","King George County"),("101","King William County"),
        ("103","Lancaster County"),("105","Lee County"),("107","Loudoun County"),("109","Louisa County"),("111","Lunenburg County"),
        ("113","Madison County"),("115","Mathews County"),("117","Mecklenburg County"),("119","Middlesex County"),("121","Montgomery County"),
        ("125","Nelson County"),("127","New Kent County"),("131","Northampton County"),("133","Northumberland County"),("135","Nottoway County"),
        ("137","Orange County"),("139","Page County"),("141","Patrick County"),("143","Pittsylvania County"),("145","Powhatan County"),
        ("147","Prince Edward County"),("149","Prince George County"),("153","Prince William County"),("155","Pulaski County"),("157","Rappahannock County"),
        ("159","Richmond County"),("161","Roanoke County"),("163","Rockbridge County"),("165","Rockingham County"),("167","Russell County"),
        ("169","Scott County"),("171","Shenandoah County"),("173","Smyth County"),("175","Southampton County"),("177","Spotsylvania County"),
        ("179","Stafford County"),("181","Surry County"),("183","Sussex County"),("185","Tazewell County"),("187","Warren County"),
        ("191","Washington County"),("193","Westmoreland County"),("195","Wise County"),("197","Wythe County"),("199","York County"),
        ("510","Alexandria city"),("520","Bristol city"),("530","Buena Vista city"),("540","Charlottesville city"),("550","Chesapeake city"),
        ("570","Colonial Heights city"),("580","Covington city"),("590","Danville city"),("595","Emporia city"),("600","Fairfax city"),
        ("610","Falls Church city"),("620","Franklin city"),("630","Fredericksburg city"),("640","Galax city"),("650","Hampton city"),
        ("660","Harrisonburg city"),("670","Hopewell city"),("678","Lexington city"),("680","Lynchburg city"),("683","Manassas city"),
        ("685","Manassas Park city"),("690","Martinsville city"),("700","Newport News city"),("710","Norfolk city"),("720","Norton city"),
        ("730","Petersburg city"),("735","Poquoson city"),("740","Portsmouth city"),("750","Radford city"),("760","Richmond city"),
        ("770","Roanoke city"),("775","Salem city"),("790","Staunton city"),("800","Suffolk city"),("810","Virginia Beach city"),
        ("820","Waynesboro city"),("830","Williamsburg city"),("840","Winchester city"),
    ];

    private static readonly (string Postal, string Fips, string Name)[] RegionStates =
        [("DE","10","Delaware"),("DC","11","District of Columbia"),("MD","24","Maryland"),("PA","42","Pennsylvania"),("VA","51","Virginia"),("WV","54","West Virginia")];

    private void Record(string text) { evidence.Add(text); output.WriteLine(text); }
    /// <summary>An exploratory expectation: recorded and reported at the end, so one gap does not hide the rest of the scenario.</summary>
    private bool Expect(bool ok, string message) { if (!ok) { unexpected.Add(message); Record("UNEXPECTED: " + message); } return ok; }
    private static FormUrlEncodedContent Form(params (string Key, object? Value)[] values)
        => new(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value?.ToString() ?? "")));
    private static async Task<object?> Sql(string text, params (string Name, object? Value)[] values)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var cmd = new SqlCommand(text, c); foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }
    private static async Task<int> Count(string text, params (string Name, object? Value)[] values) => (int)(await Sql(text, values))!;
    private static async Task Saved(HttpResponseMessage response)
    {
        using (response) Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK,
            $"{response.StatusCode}: {Excerpt(await response.Content.ReadAsStringAsync())}");
    }
    private static async Task<(HttpStatusCode Code, string Body)> Try(Task<HttpResponseMessage> call)
    {
        using var response = await call; return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
    private static string Excerpt(string html)
    {
        var errors = Regex.Matches(html, "class=\"usa-error-message\"[^>]*>([^<]+)<|role=\"alert\"[^>]*>\\s*(?:<[^>]+>\\s*)*([^<]+)<").Select(m => (m.Groups[1].Value + m.Groups[2].Value).Trim()).Where(s => s.Length > 0);
        string joined = string.Join(" | ", errors);
        return joined.Length > 0 ? joined : html[..Math.Min(400, html.Length)];
    }
    private readonly HashSet<Guid> marylandRecords = [];
    /// <summary>Maryland record ids, or Maryland patient names outside echoed query text (search boxes and links repeat the query).</summary>
    private bool MarylandData(string body)
    {
        if (marylandRecords.Any(id => body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))) return true;
        string cleaned = Regex.Replace(body, "value=\"[^\"]*\"|[?&]q=[^\"&]*|q%3D[^\"&%]*", "");
        return Regex.IsMatch(cleaned, "(Harford|City|BaltCo|Testing|Partial)" + suffix, RegexOptions.None);
    }
    private static bool Denied(HttpStatusCode code) => code is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Redirect;

    [Scenario267ReplayFact]
    public async Task Region3DelegationMarylandConfigurationCountyWorkflowsIsolationAndSecurityReports()
    {
        _ = fixture.Ids;
        var host = fixture.Factory;
        suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        try
        {
            Record($"SCENARIO 267 replay {suffix}; business date {today:yyyy-MM-dd} (real clock).");
            // ---------------------------------------------------------------- 1-2: unconfigured installation, platform account
            var platform = new MeritClient(host); clients.Add(platform); await platform.SignInAsync("platform", MeritFixture.Password);
            var platformTenant = (Guid)(await Sql("SELECT homeTenantId FROM auth.appUser WHERE userName='platform'"))!;
            Assert.Equal(0, await Count("SELECT COUNT(*) FROM auth.tenant WHERE tenantCode LIKE @c", ("@c", "%-" + suffix)));
            Record("PASS 1-2: platform administrator (seeded platform role: tenant.administer, tenant.create, platform.administer, audit.view, security.investigate) signed in with password and MFA. The fixture also carries the demo data roots; every tenant below is new and starts unconfigured.");

            // ---------------------------------------------------------------- 3: platform creates Region 3 and its administrator
            var region = await Tenant(platform, platformTenant, "R3-" + suffix, "EPA Region 3 " + suffix, "region");
            var regionalId = await CreateUser(platform, region, "r3admin" + suffix, "administrator");
            await Grant(platform, regionalId, region, "supervisor");
            var regional = await SignIn("r3admin" + suffix);
            Record("PASS 3: platform created the Region 3 tenant and its administrator (administrator + supervisor at the region; supervisor supplies the clinical rights later delegated downward).");

            // ---------------------------------------------------------------- 4: region creates every Region 3 state and its administrator
            var states = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var stateAdmins = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var (postal, fips, name) in RegionStates)
            {
                string counties = postal switch
                {
                    "MD" => string.Join("\n", MarylandCounties.Select(c => c.Fips + " " + c.Name)),
                    "VA" => string.Join("\n", VirginiaCounties.Select(c => c.Fips + " " + c.Name)),
                    _ => "",
                };
                states[postal] = await Tenant(regional, region, postal + "-" + suffix, name + " " + suffix, "state", postal, fips, newCounties: counties);
                stateAdmins[postal] = await CreateUser(regional, states[postal], postal.ToLowerInvariant() + "admin" + suffix, "administrator");
                await Grant(regional, stateAdmins[postal], states[postal], "supervisor");
            }
            mdRoot = states["MD"];
            Assert.Equal(24, await Count("SELECT COUNT(*) FROM config.subJurisdiction WHERE rootTenantId=@r AND isDeleted=0", ("@r", mdRoot)));
            Assert.Equal(133, await Count("SELECT COUNT(*) FROM config.subJurisdiction WHERE rootTenantId=@r AND isDeleted=0", ("@r", states["VA"])));
            Assert.Equal(0, await Count("SELECT COUNT(*) FROM config.notificationRule WHERE rootTenantId=@r", ("@r", mdRoot)));
            Assert.Equal(0, await Count("SELECT COUNT(*) FROM config.threshold WHERE rootTenantId=@r AND isActive=1", ("@r", mdRoot)));
            Assert.Equal(0, await Count("SELECT COUNT(*) FROM dbo.intakeProfile WHERE rootTenantId=@r", ("@r", mdRoot)));
            Record("PASS 4: Region 3 administrator created DE, DC, MD, PA, VA and WV as data roots with their administrators. Maryland carries its 24 county-level jurisdictions and Virginia its 133 counties and independent cities. Maryland starts with no rules, active thresholds or import formats.");

            // ---------------------------------------------------------------- 5: Maryland administrator creates all county administrators
            var mdAdmin = await SignIn("mdadmin" + suffix);
            var md = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var mdCountyAdmins = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var (fips, name) in MarylandCounties)
            {
                md[fips] = await Tenant(mdAdmin, mdRoot, "MD" + fips + "-" + suffix, name + " Health Department", "jurisdiction", countyFips: fips);
                mdCountyAdmins[fips] = await CreateUser(mdAdmin, md[fips], "md" + fips + "admin" + suffix, "administrator");
                await Grant(mdAdmin, mdCountyAdmins[fips], md[fips], "supervisor");
            }
            Record("PASS 5: Maryland administrator created a local health department tenant and an administrator for each of the 24 Maryland jurisdictions (23 counties and Baltimore City).");

            // ---------------------------------------------------------------- 6-8: county administrators create their staff
            var staff = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var (fips, label) in new[] { ("510", "city"), ("005", "baltco"), ("025", "harford") })
            {
                var countyAdmin = await SignIn("md" + fips + "admin" + suffix);
                foreach (var role in new[] { "supervisor", "caseManager", "intakeClerk" })
                    staff[label + role] = await CreateUser(countyAdmin, md[fips], label + role.ToLowerInvariant() + suffix, role);
            }
            Record("PASS 6-8: Baltimore City, Baltimore County and Harford County administrators each created a supervisor, a case manager and an intake clerk in their own jurisdiction.");

            // ---------------------------------------------------------------- 9: Maryland administrator creates two-county staff
            foreach (var role in new[] { "supervisor", "caseManager", "intakeClerk" })
            {
                staff["dual" + role] = await CreateUser(mdAdmin, md["005"], "dual" + role.ToLowerInvariant() + suffix, role);
                await Grant(mdAdmin, staff["dual" + role], md["025"], role);
            }
            Record("PASS 9: Maryland administrator created a supervisor, case manager and intake clerk who each work in both Baltimore County and Harford County (one grant per county).");

            // ---------------------------------------------------------------- 10-11: Virginia administrator creates every county, its administrator and staff
            var vaAdmin = await SignIn("vaadmin" + suffix);
            var va = new Dictionary<string, Guid>(StringComparer.Ordinal);
            int vaAccounts = 0;
            foreach (var (fips, name) in VirginiaCounties)
            {
                va[fips] = await Tenant(vaAdmin, states["VA"], "VA" + fips + "-" + suffix, name + " Health District", "jurisdiction", countyFips: fips);
                var countyAdmin = await CreateUser(vaAdmin, va[fips], "va" + fips + "admin" + suffix, "administrator");
                await Grant(vaAdmin, countyAdmin, va[fips], "supervisor");
                foreach (var role in new[] { "supervisor", "caseManager", "intakeClerk" })
                    await CreateUser(vaAdmin, va[fips], "va" + fips + role.ToLowerInvariant() + suffix, role);
                vaAccounts += 4;
            }
            Assert.Equal(133 * 4, vaAccounts);
            Record($"PASS 10-11: Virginia administrator created all 133 county/city tenants, an administrator for each, and a supervisor, case manager and intake clerk for each ({vaAccounts} accounts).");
            Record($"COUNT: {await Count("SELECT COUNT(*) FROM auth.appUser WHERE userName LIKE @s", ("@s", "%" + suffix))} accounts created by this replay.");

            // ---------------------------------------------------------------- 12: Maryland configuration (#202 decisions)
            foreach (var (key, value) in new[] { ("statePostalCode","MD"),("stateFips","24"),("adultAgeCutoffYears","18"),("investigationLookbackDays","365"),("contactAttemptsBeforeLetter","3") })
                await Saved(await mdAdmin.NoRedirect.PostAsync("/config/settings/" + key, Form(("settingValue", value), ("effectiveFrom", "2020-01-01"))));
            await mdAdmin.Http.GetStringAsync("/reporters");
            clia = "21D0267" + string.Concat(suffix[..3].Select(ch => char.IsAsciiDigit(ch) ? ch : (char)('0' + (ch - 'A') % 10)));
            await Saved(await mdAdmin.NoRedirect.PostAsync("/reporters", Form(("Kind", "laboratory"), ("Name", "Scenario 267 laboratory"), ("Clia", clia))));
            lab = (Guid)(await Sql("SELECT TOP(1) organizationId FROM dbo.organization WHERE rootTenantId=@r AND organizationName='Scenario 267 laboratory' AND isDeleted=0", ("@r", mdRoot)))!;
            specimen = (Guid)(await Sql("SELECT s.analyteSpecimenId FROM config.analyteSpecimen s JOIN config.analyte a ON a.analyteId=s.analyteId WHERE a.code='PB' AND s.specimen='wholeBlood'"))!;
            analyte = (Guid)(await Sql("SELECT analyteId FROM config.analyteSpecimen WHERE analyteSpecimenId=@id", ("@id", specimen)))!;
            // Reference level (§ 6-801 / COMAR 26.16.08.03): 5 ug/dL 2020-07-01..2023-12-31, 3.5 from 2024-01-01; child = under 6.
            await Threshold(mdAdmin, "5", "2020-07-01", "2023-12-31");
            await Threshold(mdAdmin, "3.5", "2024-01-01", null);
            var rules = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var key in Merit.Application.Services.MarylandPolicyDrafts.Names.Keys) rules[key] = await Template(mdAdmin, key);
            var hl7 = await Format(mdAdmin, "hl7", []);
            var csv = await Format(mdAdmin, "csv", ["lastName","firstName","dob","sex","loinc","value","unit","collectedDate","reportedDate","finalResultDate","accessionNumber","sampleType","resultStatus","addressLine1","city","state","zip5","countyFips","guardianName","guardianPhone"]);
            Assert.Equal(11, await Count("SELECT COUNT(*) FROM config.notificationRule WHERE rootTenantId=@r AND isActive=1", ("@r", mdRoot)));
            Record("PASS 12: Maryland administrator configured settings, a reporting laboratory, the reference-level history (5 ug/dL July 2020–2023, 3.5 from 2024) and activated all 11 shipped Maryland drafts (child, pregnant minor/adult, residence owner, affected property, lab elevated/below-reference reporting and the two historical child-notice windows) with explicit synthetic calendars and approvals, plus HL7 and CSV import formats.");

            // ---------------------------------------------------------------- 13: profiles for Maryland accounts
            int profiles = 0;
            foreach (var (user, tenants) in new (string, (Guid Tenant, string Role)[])[]
            {
                ("mdadmin" + suffix, [(mdRoot, "administrator")]),
                ("md510admin" + suffix, [(md["510"], "administrator")]), ("md005admin" + suffix, [(md["005"], "administrator")]), ("md025admin" + suffix, [(md["025"], "administrator")]),
                ("citycasemanager" + suffix, [(md["510"], "caseManager")]), ("baltcocasemanager" + suffix, [(md["005"], "caseManager")]), ("harfordcasemanager" + suffix, [(md["025"], "caseManager")]),
                ("harfordintakeclerk" + suffix, [(md["025"], "intakeClerk")]), ("cityintakeclerk" + suffix, [(md["510"], "intakeClerk")]),
                ("dualintakeclerk" + suffix, [(md["005"], "intakeClerk"), (md["025"], "intakeClerk")]),
                ("dualcasemanager" + suffix, [(md["005"], "caseManager"), (md["025"], "caseManager")]),
            })
            {
                var client = await SignIn(user);
                await Profile(client, "Read only", tenants.Select(t => (t.Tenant, t.Role, "read")).ToArray());
                await Profile(client, "Write, no delete", tenants.Select(t => (t.Tenant, t.Role, "write")).ToArray());
                profiles += 2;
                if (tenants.Length == 2)
                {
                    await Profile(client, "Baltimore County only", [(tenants[0].Tenant, tenants[0].Role, "full")]);
                    await Profile(client, "Harford County only", [(tenants[1].Tenant, tenants[1].Role, "full")]);
                    profiles += 2;
                }
            }
            Record($"PASS 13: {profiles} additional profiles created through 'My profiles' by Maryland, Baltimore City, Baltimore County and Harford County accounts (read only, write, and single-county profiles for the two-county staff).");

            // ---------------------------------------------------------------- 14: two-county intake (HL7, CSV, manual) and profile CRUD
            var dualClerk = signedIn["dualintakeclerk" + suffix];
            // County-scoped intake probe: the clerk holds grants only at Baltimore County and Harford County, not at the Maryland root.
            string countyHl7 = "unknown", countyManual = "unknown";
            try
            {
                await Upload(dualClerk, staff["dualintakeClerk"], hl7, Hl7("Probe" + suffix, "6.0", "025", "Bel Air", "21014", today.AddDays(-3), today.AddDays(-2), ageMonths: 47), "probe.hl7", today);
                countyHl7 = "loaded";
            }
            catch (Exception ex) { countyHl7 = ex.GetType().Name + ": " + ex.Message.Split('\n')[0] + (ex.InnerException is null ? "" : " / " + ex.InnerException.Message.Split('\n')[0]); }
            var probeChild = await Person(dualClerk, "ProbeManual" + suffix, today.AddMonths(-52));
            await dualClerk.Http.GetStringAsync($"/people/{probeChild}/results/new");
            var manualProbe = await Try(dualClerk.NoRedirect.PostAsync($"/people/{probeChild}/results", Form(("AnalyteId", analyte), ("AnalyteSpecimenId", specimen), ("ValueText", "5.0"), ("Unit", "ug/dL"), ("SampleType", "venous"),
                ("CollectedDate", today.AddDays(-3).ToString("yyyy-MM-dd")), ("ReportedDate", today.AddDays(-1).ToString("yyyy-MM-dd")), ("FinalResultDate", today.AddDays(-1).ToString("yyyy-MM-dd")), ("DepartmentReceivedAt", today.AddDays(-1).ToString("yyyy-MM-dd") + "T10:00"), ("LabOrganizationId", lab), ("AccessionNumber", suffix + "-probe"), ("ResultStatus", "final"))));
            countyManual = $"HTTP {(int)manualProbe.Code}, saved results {await Count("SELECT COUNT(*) FROM dbo.labResult WHERE accessionNumber=@a", ("@a", suffix + "-probe"))}, receipt tasks {await Count("SELECT COUNT(*) FROM dbo.workTask w JOIN dbo.labResult r ON r.labResultId=w.labResultId WHERE r.accessionNumber=@a AND w.queueCode IN ('labReportingReview','receiptFinding','missingInformation')", ("@a", suffix + "-probe"))}";
            bool countyIntakeWorks = countyHl7 == "loaded" && manualProbe.Code is HttpStatusCode.Redirect or HttpStatusCode.OK;
            Record($"{(countyIntakeWorks ? "PASS" : "DEFECT")} 14 county-scoped intake (#268): two-county clerk HL7 import → {countyHl7}; manual entry → {countyManual}.");
            Expect(countyIntakeWorks, "14: county-scoped intake clerk cannot import or enter results (#268)");
            // Statewide clerk for the remaining data entry, so the county workflows below still run.
            staff["mdintakeClerk"] = await CreateUser(mdAdmin, mdRoot, "mdintakeclerk" + suffix, "intakeClerk");
            var intakeClerk = countyIntakeWorks ? dualClerk : await SignIn("mdintakeclerk" + suffix);
            var intakeUser = countyIntakeWorks ? staff["dualintakeClerk"] : staff["mdintakeClerk"];
            var baltcoHl7 = await Upload(intakeClerk, intakeUser, hl7, Hl7("BaltCo" + suffix, "6.2", "005", "Towson", "21204", today.AddDays(-3), today.AddDays(-2)), "baltco.hl7", today);
            var harfordCsvText = "lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,finalResultDate,accessionNumber,sampleType,resultStatus,addressLine1,city,state,zip5,countyFips,guardianName,guardianPhone\r\n"
                + $"Harford{suffix},Ada,{today.AddMonths(-30):yyyy-MM-dd},female,77307-7,7.5,ug/dL,{today.AddDays(-14):yyyy-MM-dd},{today.AddDays(-1):yyyy-MM-dd},{today.AddDays(-12):yyyy-MM-dd},H267-{suffix},venous,final,25 Harford Way,Bel Air,MD,21014,025,Grace Harford{suffix},4105550267\r\n"
                + $"Partial{suffix},Pat,{today.AddMonths(-40):yyyy-MM-dd},,77307-7,2.1,ug/dL,{today.AddDays(-9):yyyy-MM-dd},{today.AddDays(-1):yyyy-MM-dd},{today.AddDays(-2):yyyy-MM-dd},P267-{suffix},capillary,final,,,MD,,025,,\r\n";
            var harfordCsv = await Upload(intakeClerk, intakeUser, csv, harfordCsvText, "harford.csv", today, expectedLoaded: 2);
            var baltcoPerson = await PersonOf(baltcoHl7);
            var harfordPerson = (Guid)(await Sql("SELECT personId FROM dbo.labResult WHERE accessionNumber=@a AND rootTenantId=@r", ("@a", "H267-" + suffix), ("@r", mdRoot)))!;
            var partialResult = (Guid)(await Sql("SELECT labResultId FROM dbo.labResult WHERE accessionNumber=@a AND rootTenantId=@r", ("@a", "P267-" + suffix), ("@r", mdRoot)))!;
            var harfordResult = (Guid)(await Sql("SELECT labResultId FROM dbo.labResult WHERE accessionNumber=@a AND rootTenantId=@r", ("@a", "H267-" + suffix), ("@r", mdRoot)))!;
            Record("OBSERVE intake owners: HL7 Baltimore County person " + await Sql("SELECT t.tenantName FROM dbo.person p JOIN auth.tenant t ON t.tenantId=p.owningTenantId WHERE p.personId=@p", ("@p", baltcoPerson))
                + "; CSV Harford person " + await Sql("SELECT t.tenantName FROM dbo.person p JOIN auth.tenant t ON t.tenantId=p.owningTenantId WHERE p.personId=@p", ("@p", harfordPerson)));
            var baltcoCase = await CaseOf(baltcoHl7);
            var harfordCase = await CaseOf(harfordResult);
            Assert.Equal(md["005"], await Sql("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", baltcoCase)));
            Assert.Equal(md["025"], await Sql("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", harfordCase)));
            Record($"PASS 14a: {(countyIntakeWorks ? "the two-county" : "the statewide (workaround for #268)")} intake clerk imported an HL7 Baltimore County result and a CSV Harford County result; each case routed to its county health department.");
            string late = (await Sql("SELECT STRING_AGG(CONCAT(queueCode,'|',taskStatus,'|',title),' ;; ') FROM dbo.workTask WHERE labResultId=@id AND queueCode IN ('receiptFinding','missingInformation','labReportingReview') AND isDeleted=0", ("@id", harfordResult)))?.ToString() ?? "";
            string missing = (await Sql("SELECT STRING_AGG(CONCAT(queueCode,'|',taskStatus,'|',title,'|',LEFT(detail,160)),' ;; ') FROM dbo.workTask WHERE labResultId=@id AND queueCode IN ('receiptFinding','missingInformation','labReportingReview') AND isDeleted=0", ("@id", partialResult)))?.ToString() ?? "";
            Record("OBSERVE late elevated CSV report (final result 12 days before receipt): " + late);
            Record("OBSERVE incomplete below-reference CSV report: " + missing);
            Assert.Contains("receiptFinding|done|Late laboratory report detected", late, StringComparison.Ordinal);
            Assert.Contains("receiptFinding|done|Missing information detected", missing, StringComparison.Ordinal);
            Assert.Contains("missingInformation|open|Missing Information Received", missing, StringComparison.Ordinal);
            Record("PASS 14b: reviewer notes for COMAR 26.02.01.03/.04 — a late elevated report is recorded as a completed 'Late laboratory report detected' task; an incomplete report gets a completed 'Missing information detected' task plus a pending 'Missing Information Received' task.");
            var notices = (await Sql("SELECT STRING_AGG(CONCAT(r.ruleName,'|',n.recipientKind,'|',n.evaluationState,'|',CONVERT(CHAR(10),n.dueDate,23)),' ;; ') FROM dbo.notificationObligation n JOIN config.notificationRule r ON r.notificationRuleId=n.notificationRuleId WHERE n.labResultId=@id", ("@id", harfordResult)))?.ToString() ?? "";
            Record("OBSERVE Harford 7.5 ug/dL child notices: " + notices);
            Assert.Contains("caregiver", notices, StringComparison.Ordinal); Assert.Contains("owner", notices, StringComparison.Ordinal);

            // Manual entry for a Harford child: the #202 A–G testing-plan scenario.
            var child = await Person(dualClerk, "Testing" + suffix, today.AddMonths(-40));
            await Place(signedIn["dualcasemanager" + suffix], child, "26 Testing Lane", "Aberdeen", "21001", "025");
            var draws = new Dictionary<char, Guid>();
            foreach (var (letter, days, value, sample) in new[] { ('A', 570, "2.0", "capillary"), ('B', 540, "2.5", "capillary"), ('C', 510, "3.0", "capillary"), ('D', 420, "3.6", "capillary"), ('E', 410, "3.1", "capillary"), ('F', 400, "5.0", "capillary") })
                draws[letter] = await Manual(intakeClerk, child, value, sample, today.AddDays(-days), $"{suffix}-{letter}");
            var dualCm = signedIn["dualcasemanager" + suffix];
            var refuseF = await Try(PlanAsync(dualCm, child, Guid.NewGuid(), "confirmation", draws['F']));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refuseF.Code);
            Assert.Contains("earlier unresolved elevated capillary", refuseF.Body, StringComparison.Ordinal);
            var confirmation = Guid.NewGuid(); await Saved(await PlanAsync(dualCm, child, confirmation, "confirmation", draws['D']));
            var confirmationDue = (DateTime)(await Sql("SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id", ("@id", confirmation)))!;
            Assert.Equal(today.AddDays(-420).AddDays(84).ToDateTime(TimeOnly.MinValue), confirmationDue);
            Record($"PASS 14c (#202 §2): with A–C below, D 3.6 capillary, E below, F 5.0 capillary, a confirmation plan from F is refused ('earlier unresolved elevated capillary'); the plan from initial draw D is due {confirmationDue:yyyy-MM-dd} (12 weeks after D's draw; MDH 2025 Table 2).");
            draws['G'] = await Manual(intakeClerk, child, "4.2", "venous", today.AddDays(-390), $"{suffix}-G");
            var early = Guid.NewGuid(); await Saved(await PlanAsync(dualCm, child, early, "early", draws['G']));
            var earlyDue = (DateTime)(await Sql("SELECT dueDate FROM dbo.workTask WHERE workTaskId=@id", ("@id", early)))!;
            Assert.Equal(today.AddDays(-390).AddMonths(3).ToDateTime(TimeOnly.MinValue), earlyDue);
            Record("OBSERVE confirmation plan after venous G: " + await Sql("SELECT CONCAT(taskStatus,'|',ISNULL(resolutionNote,'')) FROM dbo.workTask WHERE workTaskId=@id", ("@id", confirmation)));
            Record($"PASS 14d (#202 §3): venous G one month after D (4.2 ug/dL) anchors the early follow-up, due {earlyDue:yyyy-MM-dd} (3 months after G's draw).");
            var testingCase = await CaseOfPerson(child);
            draws['H'] = await Manual(intakeClerk, child, "2.0", "venous", today.AddDays(-270), $"{suffix}-H");
            draws['I'] = await Manual(intakeClerk, child, "1.5", "venous", today.AddDays(-150), $"{suffix}-I");
            var discharge = await Try(PlanAsync(dualCm, child, Guid.NewGuid(), "discharge", draws['I'], testingCase, remediation: true));
            Record($"OBSERVE (#202 §4) discharge readiness after venous H 2.0 and I 1.5 four months apart: HTTP {(int)discharge.Code} {Excerpt(discharge.Body)}");
            Assert.True(discharge.Code is HttpStatusCode.Redirect or HttpStatusCode.OK, Excerpt(discharge.Body));
            Record("PASS 14e (#202 §4): discharge readiness recorded after two consecutive below-reference tests at least 3 months apart with remediation attested.");

            // Profiles: single-county and read-only limits.
            var baltcoOnly = await ProfileId("dualintakeclerk" + suffix, "Baltimore County only");
            await Switch(dualClerk, baltcoOnly);
            var crossCounty = await Try(dualClerk.NoRedirect.GetAsync($"/people/{harfordPerson}"));
            var ownCounty = await Try(dualClerk.NoRedirect.GetAsync($"/people/{baltcoPerson}"));
            Expect(Denied(crossCounty.Code) && !crossCounty.Body.Contains("Harford" + suffix, StringComparison.Ordinal), $"14: Baltimore County-only profile opened a Harford patient ({(int)crossCounty.Code})");
            Expect(ownCounty.Code == HttpStatusCode.OK, $"14: Baltimore County-only profile could not open its own Baltimore County patient ({(int)ownCounty.Code})");
            var readOnly = await ProfileId("dualintakeclerk" + suffix, "Read only");
            await Switch(dualClerk, readOnly);
            var readOnlyForm = await Try(dualClerk.NoRedirect.GetAsync("/people/new"));
            var readOnlyCreate = await Try(dualClerk.NoRedirect.PostAsync("/people/new", Form(("LastName", "ReadOnly" + suffix), ("FirstName", "Ada"), ("Dob", "2023-01-01"), ("Sex", "female"), ("Ethnicity", "unknown"))));
            Expect(await Count("SELECT COUNT(*) FROM dbo.person WHERE lastName=@n", ("@n", "ReadOnly" + suffix)) == 0, $"14: read-only profile created a patient ({(int)readOnlyCreate.Code})");
            int before14f = unexpected.Count(u => u.StartsWith("14:", StringComparison.Ordinal) && !u.Contains("#268", StringComparison.Ordinal));
            Record($"{(before14f == 0 ? "PASS" : "FAIL")} 14f (#279): two-county clerk's 'Baltimore County only' profile opens the Baltimore County patient and cannot open the Harford patient ({(int)crossCounty.Code}); 'Read only' cannot open the new-patient form ({(int)readOnlyForm.Code}) or create a patient ({(int)readOnlyCreate.Code}).");
            await Switch(dualClerk, null);

            // ---------------------------------------------------------------- 15-17: county case management and investigation
            var cityClerk = await SignIn("cityintakeclerk" + suffix);
            var cityPerson = await Person(cityClerk, "City" + suffix, today.AddMonths(-28));
            await Place(signedIn["citycasemanager" + suffix], cityPerson, "510 Charm City Ave", "Baltimore", "21201", "510");
            var cityResult = await Manual(countyIntakeWorks ? cityClerk : intakeClerk, cityPerson, "12.0", "venous", today.AddDays(-4), suffix + "-city");
            var cityCase = await CaseOf(cityResult);
            Record("OBSERVE Baltimore City manual case owner: " + await Sql("SELECT t.tenantName FROM dbo.exposureCase c JOIN auth.tenant t ON t.tenantId=c.owningTenantId WHERE c.exposureCaseId=@c", ("@c", cityCase)));
            foreach (var (label, caseId, person, address, county) in new[] {
                ("baltco", baltcoCase, baltcoPerson, "9900 Synthetic Way", "005"), ("harford", harfordCase, harfordPerson, "25 Harford Way", "025"), ("city", cityCase, cityPerson, "510 Charm City Ave", "510") })
                await CountyWorkflow(label, caseId, person, county, md);

            // ---------------------------------------------------------------- 18: Virginia accounts cannot reach Maryland records
            var vaCounty = await SignIn("va059admin" + suffix);
            var vaCm = await SignIn("va059casemanager" + suffix);
            var vaSup = await SignIn("va510supervisor" + suffix);
            var targets = new List<string> { $"/people/{harfordPerson}", $"/people/{baltcoPerson}", $"/people/{cityPerson}", $"/people/{child}", $"/cases/{harfordCase}", $"/cases/{baltcoCase}", $"/cases/{cityCase}",
                $"/people/{harfordPerson}/care/testing", $"/results/{harfordResult}" };
            foreach (var inv in await Investigations()) targets.Add($"/investigations/{inv}");
            foreach (var id in new[] { harfordPerson, baltcoPerson, cityPerson, child, harfordCase, baltcoCase, cityCase, harfordResult }) marylandRecords.Add(id);
            foreach (var inv in await Investigations()) marylandRecords.Add(inv);
            int probes = 0;
            foreach (var (name, client) in new[] { ("vaadmin", vaAdmin), ("va059admin", vaCounty), ("va059casemanager", vaCm), ("va510supervisor", vaSup) })
            {
                foreach (var url in targets)
                {
                    var r = await Try(client.NoRedirect.GetAsync(url)); probes++;
                    Expect(!Denied(r.Code) ? !MarylandData(r.Body) : true, $"18: {name} opened {url} ({(int)r.Code})");
                }
                foreach (var search in new[] { $"/search?q=Harford{suffix}", $"/search?q=City{suffix}", "/cases", "/investigations", "/testing", "/testing.csv" })
                {
                    var r = await Try(client.NoRedirect.GetAsync(search)); probes++;
                    Expect(!MarylandData(r.Body), $"18: {name} saw Maryland patients through {search} ({(int)r.Code})");
                    if (search.StartsWith("/search", StringComparison.Ordinal))
                        Expect(r.Body.Contains("No matches in your access scope", StringComparison.Ordinal) && !r.Body.Contains("exists in another jurisdiction", StringComparison.Ordinal), $"18: {name} search {search} did not report an empty scope");
                }
            }
            Record($"PASS 18: Virginia state, county administrator, case manager and supervisor accounts made {probes} attempts on Maryland patients, cases, results, testing plans and investigations, by direct link and through search, lists and exports. None returned Maryland data.");

            // ---------------------------------------------------------------- 19: Region 3 profiles and CRUD
            await Profile(regional, "Region read only", [(region, "administrator", "read")]);
            await Profile(regional, "Region accounts only", [(region, "administrator", "full")]);
            await Profile(regional, "Region clinical read", [(region, "supervisor", "read")]);
            var regionReadOnly = await ProfileId("r3admin" + suffix, "Region read only");
            await Switch(regional, regionReadOnly);
            await regional.Http.GetAsync("/admin/users/new");
            var roUser = await Try(regional.NoRedirect.PostAsync("/admin/users", Form(("UserName", "r3ro" + suffix), ("DisplayName", "r3ro"), ("NewPassword", Password), ("HomeTenantId", region), ("IsActive", "true"), ("RequirePasswordChange", "false"))));
            var roTenant = await Try(regional.NoRedirect.PostAsync("/admin/tenants", Form(("ParentTenantId", region), ("TenantCode", "R3RO-" + suffix), ("TenantName", "ro"), ("TenantType", "state"), ("StatePostalCode", "NJ"), ("StateFips", "34"), ("TimeZone", "Eastern Standard Time"), ("SeedThresholds", "false"))));
            Expect(await Count("SELECT COUNT(*) FROM auth.appUser WHERE userName=@n", ("@n", "r3ro" + suffix)) == 0, $"19: Region read-only profile created an account ({(int)roUser.Code})");
            Expect(await Count("SELECT COUNT(*) FROM auth.tenant WHERE tenantCode=@n", ("@n", "R3RO-" + suffix)) == 0, $"19: Region read-only profile created a state ({(int)roTenant.Code})");
            Record($"PASS 19a: Region 3 'Region read only' profile cannot create an account ({(int)roUser.Code}) or a state ({(int)roTenant.Code}).");
            await Switch(regional, await ProfileId("r3admin" + suffix, "Region accounts only"));
            await CreateUser(regional, states["DE"], "deextra" + suffix, "userAdministrator");
            Record("PASS 19b: 'Region accounts only' (full) profile created a Delaware user administrator.");
            var regionView = await Try(regional.NoRedirect.GetAsync($"/people/{harfordPerson}"));
            Record($"OBSERVE 19c: Region 3 account opening a Maryland patient: HTTP {(int)regionView.Code}; Maryland data shown: {regionView.Body.Contains("Harford" + suffix, StringComparison.Ordinal)}");
            await Switch(regional, null);

            // ---------------------------------------------------------------- failed sign-ins for the security reports
            foreach (var name in new[] { "harfordcasemanager" + suffix, "citycasemanager" + suffix, "va059casemanager" + suffix, "nosuchuser" + suffix })
            {
                var stray = new MeritClient(host); clients.Add(stray);
                var page = await stray.Http.GetStringAsync("/account/signin");
                using var _r = await stray.Http.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", name), ("Password", "Wrong!" + suffix)));
            }

            // ---------------------------------------------------------------- 20-21: security reports for Region 3 and the platform
            await SecurityReports("Region 3 administrator", regional);
            await SecurityReports("Platform administrator", platform);

            // ---------------------------------------------------------------- 22: Harford County security officer
            var harfordAdmin = signedIn["md025admin" + suffix];
            var officer = await CreateUser(harfordAdmin, md["025"], "harfordsecurity" + suffix, "caseManager");
            var platformRole = (Guid)(await Sql("SELECT roleId FROM auth.role WHERE roleCode='platformAdministrator' AND isDeleted=0"))!;
            await platform.Http.GetStringAsync($"/admin/users/{officer}");
            var refusedPost = await Try(platform.NoRedirect.PostAsync($"/admin/users/{officer}/assignments", Form(("roleId", platformRole), ("tenantId", md["025"]), ("includeDescendants", "true"), ("accessLevel", "full"))));
            var refusal = await platform.Http.GetStringAsync($"/admin/users/{officer}");
            Assert.Equal(0, await Count("SELECT COUNT(*) FROM auth.userAssignment WHERE appUserId=@u AND roleId=@r AND isDeleted=0", ("@u", officer), ("@r", platformRole)));
            Record($"OBSERVE 22a (#271): platform granting its own role (the only role with security.investigate) to the Harford account: POST HTTP {(int)refusedPost.Code}: {WebUtility.HtmlDecode(Excerpt(refusedPost.Body))}; user page message: " + (WebUtility.HtmlDecode(Regex.Match(refusal, "usa-alert--error[^>]*><div[^>]*><p[^>]*>([^<]*)").Groups[1].Value) is { Length: > 0 } m ? m : "(none)"));
            var securityRoles = (await Sql("SELECT STRING_AGG(r.roleCode,',') FROM auth.role r WHERE r.rootTenantId=@r AND r.isDeleted=0 AND EXISTS (SELECT 1 FROM auth.rolePermission rp JOIN auth.permission p ON p.permissionId=rp.permissionId WHERE rp.roleId=r.roleId AND rp.isDeleted=0 AND p.permissionCode='security.investigate')", ("@r", mdRoot)))?.ToString();
            Record("OBSERVE 22b (#271): Maryland roles holding security.investigate: " + (securityRoles ?? "(none)") + " (unknown sign-in names match no account, so they belong to no tenant and stay platform-only)");
            Expect(refusal.Contains("Platform accounts hold only platform roles", StringComparison.Ordinal), "22: the refused platform-role grant is not explained on the user page (#271)");
            await Grant(platform, officer, md["025"], "securityOfficer");
            Assert.Equal(1, await Count("SELECT COUNT(*) FROM auth.userAssignment ua JOIN auth.role r ON r.roleId=ua.roleId WHERE ua.appUserId=@u AND r.roleCode='securityOfficer' AND ua.tenantId=@t AND ua.isDeleted=0", ("@u", officer), ("@t", md["025"])));
            Record("PASS 22c (#271): platform granted the Harford account the Security officer role (audit.view, usage.view, security.review: read-only security reports) at Harford County only.");

            // ---------------------------------------------------------------- 23: Harford security officer reports
            var security = await SignIn("harfordsecurity" + suffix);
            await security.Http.GetStringAsync($"/people/{harfordPerson}"); // the officer is also a Harford case manager
            await SecurityReports("Harford County security officer", security, harford: true);
            Record(unexpected.Count == 0 ? "RESULT: no unexpected behaviour." : $"RESULT: {unexpected.Count} unexpected: " + string.Join(" || ", unexpected));
            // #268-#272 are fixed: any unexpected behaviour is a new finding.
            Assert.Empty(unexpected);
        }
        finally
        {
            foreach (var client in clients)
            {
                try { using var signedOut = await client.NoRedirect.PostAsync("/account/signout", Form()); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                finally { client.Dispose(); }
            }
            await TestSessionScopes.RevokeAllAsync();
            string path = Path.Combine(TestEnvironment.SourceRoot, ".artifacts", "scenario-267-replay-evidence.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllLinesAsync(path, evidence, Encoding.UTF8);
        }
    }

    // ==================================================================== county workflow (15-17)
    private async Task CountyWorkflow(string label, Guid caseId, Guid person, string county, Dictionary<string, Guid> md)
    {
        var supervisor = await SignIn(label + "supervisor" + suffix);
        var manager = await SignIn(label + "casemanager" + suffix);
        var clerk = await SignIn(label + "intakeclerk" + suffix);
        string name = label + "casemanager" + suffix;
        var managerId = (Guid)(await Sql("SELECT appUserId FROM auth.appUser WHERE userName=@n", ("@n", name)))!;
        Assert.Equal(md[county], await Sql("SELECT owningTenantId FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", caseId)));

        var page = await supervisor.Http.GetStringAsync($"/cases/{caseId}");
        Expect(page.Contains(managerId.ToString(), StringComparison.Ordinal), $"{label}: county case manager is not offered for assignment");
        await Saved(await supervisor.NoRedirect.PostAsync($"/cases/{caseId}/update", Form(("assignedToUserId", managerId), ("exposureCategory", "residential"), ("caseStatus", "open"), ("rowVersion", await RowVersion(caseId)))));
        Assert.Equal(managerId, await Sql("SELECT assignedToUserId FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", caseId)));

        await manager.Http.GetStringAsync($"/cases/{caseId}");
        await Saved(await manager.NoRedirect.PostAsync($"/cases/{caseId}/activities", Form(("activityType", "contactAttempt"), ("activityDate", today.ToString("yyyy-MM-dd")), ("contactPersonId", person), ("outcome", "reached"), ("note", "Synthetic call to caregiver"))));
        await Saved(await manager.NoRedirect.PostAsync($"/cases/{caseId}/activities", Form(("activityType", "education"), ("activityDate", today.ToString("yyyy-MM-dd")), ("contactPersonId", person), ("outcome", "completed"), ("note", "Synthetic lead education"))));
        Assert.True(await Count("SELECT COUNT(*) FROM dbo.caseActivity WHERE exposureCaseId=@c AND isDeleted=0", ("@c", caseId)) >= 2);

        // Investigation of the residence.
        var location = await Sql("SELECT TOP(1) locationId FROM dbo.personLocation WHERE personId=@p AND isDeleted=0 AND endDate IS NULL ORDER BY createdDateUtc DESC", ("@p", person));
        if (location is null)
        {
            await Place(manager, person, "1 Residence Rd", "Anytown", county == "510" ? "21201" : "21014", county);
            location = await Sql("SELECT TOP(1) locationId FROM dbo.personLocation WHERE personId=@p AND isDeleted=0 ORDER BY createdDateUtc DESC", ("@p", person));
        }
        await manager.Http.GetStringAsync($"/cases/{caseId}/investigations/new");
        using (var request = await manager.NoRedirect.PostAsync($"/cases/{caseId}/investigations", Form(("ExposureCaseId", caseId), ("InvestigationType", "residential"), ("Target", "loc:" + location), ("Checked", "true"))))
            Assert.True(request.IsSuccessStatusCode, $"{label} investigation request: {request.StatusCode} {Excerpt(await request.Content.ReadAsStringAsync())}");
        var investigation = (Guid)(await Sql("SELECT TOP(1) i.investigationId FROM dbo.investigation i JOIN dbo.investigationCase ic ON ic.investigationId=i.investigationId WHERE ic.exposureCaseId=@c ORDER BY i.createdDateUtc DESC", ("@c", caseId)))!;
        Assert.Equal(md[county], await Sql("SELECT owningTenantId FROM dbo.investigation WHERE investigationId=@i", ("@i", investigation)));
        await manager.Http.GetStringAsync($"/investigations/{investigation}");
        await Saved(await manager.NoRedirect.PostAsync($"/investigations/{investigation}", Form(("investigationStatus", "scheduled"), ("assignedToUserId", managerId), ("scheduledDate", today.ToString("yyyy-MM-dd")), ("rowVersion", await InvestigationVersion(investigation)))));
        await Saved(await manager.NoRedirect.PostAsync($"/investigations/{investigation}/samples", Form(("sampleType", "dustWipe"), ("roomOrComponent", "Kitchen floor"), ("surface", "floor"), ("sampleValue", "15"), ("unit", "ug/ft2"), ("sampleDate", today.ToString("yyyy-MM-dd")), ("notes", "Synthetic sample"))));
        await Saved(await manager.NoRedirect.PostAsync($"/investigations/{investigation}", Form(("investigationStatus", "findingsIssued"), ("assignedToUserId", managerId), ("scheduledDate", today.ToString("yyyy-MM-dd")), ("completedDate", today.ToString("yyyy-MM-dd")), ("findings", "Synthetic deteriorated paint findings"), ("regulatoryAction", "Notice to owner"), ("rowVersion", await InvestigationVersion(investigation)))));
        Assert.Equal("findingsIssued", await Sql("SELECT investigationStatus FROM dbo.investigation WHERE investigationId=@i", ("@i", investigation)));
        Assert.Equal(1, await Count("SELECT COUNT(*) FROM dbo.environmentalSample WHERE investigationId=@i AND isDeleted=0", ("@i", investigation)));

        // Other profiles: read-only case manager, intake clerk, and the other counties.
        var readOnly = await ProfileId(name, "Read only");
        await Switch(manager, readOnly);
        await manager.Http.GetStringAsync($"/cases/{caseId}");
        int before = await Count("SELECT COUNT(*) FROM dbo.caseActivity WHERE exposureCaseId=@c", ("@c", caseId));
        var roActivity = await Try(manager.NoRedirect.PostAsync($"/cases/{caseId}/activities", Form(("activityType", "contactAttempt"), ("activityDate", today.ToString("yyyy-MM-dd")), ("note", "read only attempt"))));
        Expect(before == await Count("SELECT COUNT(*) FROM dbo.caseActivity WHERE exposureCaseId=@c", ("@c", caseId)), $"{label}: read-only profile logged case activity ({(int)roActivity.Code})");
        await Switch(manager, null);
        var clerkUpdate = await Try(clerk.NoRedirect.PostAsync($"/cases/{caseId}/update", Form(("assignedToUserId", ""), ("exposureCategory", "unknown"), ("caseStatus", "open"), ("rowVersion", await RowVersion(caseId)))));
        Expect(managerId.Equals(await Sql("SELECT assignedToUserId FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", caseId))), $"{label}: intake clerk changed the case ({(int)clerkUpdate.Code})");
        var clerkView = await Try(clerk.NoRedirect.GetAsync($"/cases/{caseId}"));
        string other = label == "harford" ? "city" : "harford";
        var otherSupervisor = await SignIn(other + "supervisor" + suffix);
        var crossCase = await Try(otherSupervisor.NoRedirect.GetAsync($"/cases/{caseId}"));
        var crossInvestigation = await Try(otherSupervisor.NoRedirect.GetAsync($"/investigations/{investigation}"));
        Expect(Denied(crossCase.Code), $"{label}: {other} supervisor opened the case ({(int)crossCase.Code})");
        Expect(Denied(crossInvestigation.Code), $"{label}: {other} supervisor opened the investigation ({(int)crossInvestigation.Code})");

        // Closure attempt by the supervisor, through the closure review.
        await supervisor.Http.GetStringAsync($"/cases/{caseId}");
        var token = (string)(await Sql("SELECT dbo.udfCaseClosureReviewStamp(@id)", ("@id", caseId)))!;
        var close = await Try(supervisor.NoRedirect.PostAsync($"/cases/{caseId}/close", Form(("rowVersion", await RowVersion(caseId)), ("closureReason", "other"), ("closedDate", today.ToString("yyyy-MM-dd")), ("note", "Synthetic scenario end"), ("closureReviewToken", token), ("closureReviewConfirmed", "true"))));
        string status = (string)(await Sql("SELECT caseStatus FROM dbo.exposureCase WHERE exposureCaseId=@c", ("@c", caseId)))!;
        Record($"PASS {(label == "baltco" ? 15 : label == "harford" ? 16 : 17)} ({label}): case routed to the county, supervisor assigned it to the county case manager, the case manager logged contact and education, requested and scheduled a residential investigation, recorded a dust sample and issued findings. "
            + $"Read-only profile could not log activity ({(int)roActivity.Code}); intake clerk could view ({(int)clerkView.Code}) but not update the case ({(int)clerkUpdate.Code}); {other} supervisor could not open the case ({(int)crossCase.Code}) or investigation ({(int)crossInvestigation.Code}). "
            + $"Supervisor closure through the closure review: HTTP {(int)close.Code}, case status '{status}'{(status == "closed" ? "" : ": " + Excerpt(close.Body))}.");
    }

    // ==================================================================== security reports (20, 21, 23)
    private async Task SecurityReports(string who, MeritClient client, bool harford = false)
    {
        var lines = new List<string>();
        async Task<string> Get(string url)
        {
            var r = await Try(client.NoRedirect.GetAsync(url)); lines.Add($"{url} → {(int)r.Code}"); return r.Code == HttpStatusCode.OK ? r.Body : "";
        }
        string audit = await Get("/admin/audit?pageSize=100");
        string auditCsv = await Get("/admin/audit.csv");
        string activity = await Get("/admin/sign-in-activity?hours=24");
        string unknown = await Get("/admin/sign-in-activity/unknown-names?hours=24");
        string unknownCsv = await Get("/admin/sign-in-activity/unknown-names.csv?hours=24");
        string usage = await Get("/admin/usage?days=1");
        string usageCsv = await Get("/admin/usage/summary.csv?days=1");
        string users = await Get("/admin/users");
        string activityCsv = await Get("/admin/sign-in-activity/activity.csv?hours=24");
        string usersCsv = await Get("/admin/users.csv");
        Expect(auditCsv.Length > 0 && activityCsv.Length > 0 && usersCsv.Length > 0, $"{who}: a security report export is unavailable (#272): {string.Join("; ", lines)}");
        var csvUsers = auditCsv.Split("\r\n").Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')[1]).Distinct().ToList();
        string Of(string body, string prefix) => Regex.IsMatch(body, prefix + "[a-z0-9]*" + suffix, RegexOptions.IgnoreCase) ? "yes" : "no";
        Record($"REPORTS {who}: {string.Join("; ", lines)}");
        Record($"REPORTS {who}: audit CSV rows {Math.Max(0, auditCsv.Split("\r\n").Length - 2)}, distinct users {csvUsers.Count}: {string.Join(", ", csvUsers.Take(12))}{(csvUsers.Count > 12 ? ", …" : "")}");
        Record($"REPORTS {who} (#270): audit/CSV mentions — Harford staff {Of(auditCsv, "harford")}, two-county staff {Of(auditCsv, "dual")}, Baltimore City staff {Of(auditCsv, "city")}, Virginia staff {Of(auditCsv, "va059")}, Maryland admin {Of(auditCsv, "mdadmin" + "(?=" + suffix + ")")}");
        Record($"REPORTS {who} (#270/#272): sign-in activity mentions — Harford {Of(activity, "harford")}, Baltimore City {Of(activity, "city")}, Virginia {Of(activity, "va059")}; has an export link: {activity.Contains(".csv", StringComparison.Ordinal)}");
        Record($"REPORTS {who} (#272): user list mentions — Harford {Of(users, "harford")}, two-county {Of(users, "dual")}, Baltimore City {Of(users, "city")}, Virginia {Of(users, "va059")}; has an export link: {users.Contains(".csv", StringComparison.Ordinal)}");
        Record($"REPORTS {who}: unknown names report available {unknown.Length > 0}, export {unknownCsv.Length > 0}; usage report {usage.Length > 0}, export {usageCsv.Length > 0}");
        Record($"REPORTS {who} (#272): exports — sign-in activity {activityCsv.Length > 0}, users and access {usersCsv.Length > 0}, audit {auditCsv.Length > 0}");
        if (harford)
        {
            // Expected by #267 step 23: only Harford users and other users who accessed Harford data.
            Record($"CHECK 23 (#270): Harford officer sees Baltimore City-only staff in audit: {Of(auditCsv, "city")}; in user list: {Of(users, "city")}; sees two-county staff who touched Harford data: {Of(auditCsv, "dual")}; Harford staff: {Of(auditCsv, "harford")}");
            Expect(Of(auditCsv, "city") == "no" && Of(users, "city") == "no", "23: the Harford officer's reports include Baltimore City-only staff (#270)");
            Expect(Of(auditCsv, "harford") == "yes" && Of(auditCsv, "dual") == "yes", "23: the Harford officer's audit omits Harford staff or the two-county staff who work Harford data (#270)");
            Expect(!users.Contains("/admin/users/new", StringComparison.Ordinal), "23: the read-only Security officer is offered account creation (#271)");
        }
        else
        {
            Expect(Of(auditCsv, "harford") == "yes" && Of(auditCsv, "va059") == "yes", $"{who}: the audit does not reach the state data roots below it (#270)");
        }
    }

    // ==================================================================== helpers
    private async Task<MeritClient> SignIn(string name)
    {
        if (signedIn.TryGetValue(name, out var existing)) return existing;
        var client = new MeritClient(fixture.Factory); clients.Add(client);
        var previous = clients.LastOrDefault(c => c.UserName == name && c.TotpSecret is not null);
        if (previous is null) await client.SignInAsync(name, name == "platform" ? MeritFixture.Password : Password);
        else
        {
            var page = await client.Http.GetStringAsync("/account/signin");
            using var step = await client.Http.PostAsync("/account/signin", MeritClient.Form(page, ("UserName", name), ("Password", Password)));
            using var done = await client.Http.PostAsync("/account/mfa", MeritClient.Form(await step.Content.ReadAsStringAsync(), ("Code", previous.CurrentTotpCode())));
        }
        Assert.Equal(HttpStatusCode.OK, (await client.Http.GetAsync("/api/v1/me")).StatusCode);
        signedIn[name] = client; return client;
    }
    /// <summary>A grant revokes the person's sessions: sign them in again with their enrolled authenticator.</summary>
    private async Task Refresh(Guid user)
    {
        string name = (string)(await Sql("SELECT userName FROM auth.appUser WHERE appUserId=@id", ("@id", user)))!;
        if (signedIn.Remove(name, out var old)) { var fresh = await SignIn(name); old.Dispose(); signedIn[name] = fresh; }
    }
    private async Task<Guid> Tenant(MeritClient actor, Guid parent, string code, string name, string type, string? postal = null, string? fips = null, string? countyFips = null, string? newCounties = null)
    {
        await actor.Http.GetStringAsync("/admin/tenants");
        await Saved(await actor.NoRedirect.PostAsync("/admin/tenants", Form(("ParentTenantId", parent), ("TenantCode", code), ("TenantName", name), ("TenantType", type),
            ("StatePostalCode", postal ?? ""), ("StateFips", fips ?? ""), ("TimeZone", "Eastern Standard Time"), ("EffectiveFrom", "2020-01-01"), ("SeedThresholds", "false"),
            ("CountyFipsList", countyFips ?? ""), ("NewCounties", newCounties ?? ""))));
        return (Guid)(await Sql("SELECT tenantId FROM auth.tenant WHERE tenantCode=@code", ("@code", code)))!;
    }
    private static async Task<Guid> Role(Guid tenant, string role) => (Guid)(await Sql("SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantId=@t) AND roleCode=@role AND isDeleted=0", ("@t", tenant), ("@role", role)))!;
    private async Task<Guid> CreateUser(MeritClient actor, Guid tenant, string name, string role)
    {
        var roleId = await Role(tenant, role); await actor.Http.GetStringAsync("/admin/users/new");
        await Saved(await actor.NoRedirect.PostAsync("/admin/users", Form(("UserName", name), ("DisplayName", name), ("Email", name + "@scenario267.example"), ("NewPassword", Password), ("HomeTenantId", tenant),
            ("IsActive", "true"), ("RequirePasswordChange", "false"), ("FirstRoleId", roleId), ("FirstTenantId", tenant), ("FirstIncludeDescendants", "true"), ("FirstAccessLevel", "full"))));
        var id = (Guid)(await Sql("SELECT appUserId FROM auth.appUser WHERE userName=@name", ("@name", name)))!;
        Assert.Equal(1, await Count("SELECT COUNT(*) FROM auth.userAssignment WHERE appUserId=@u AND roleId=@r AND tenantId=@t AND isDeleted=0", ("@u", id), ("@r", roleId), ("@t", tenant)));
        return id;
    }
    private async Task Grant(MeritClient actor, Guid user, Guid tenant, string role)
    {
        var roleId = await Role(tenant, role);
        await actor.Http.GetStringAsync($"/admin/users/{user}");
        await Saved(await actor.NoRedirect.PostAsync($"/admin/users/{user}/assignments", Form(("roleId", roleId), ("tenantId", tenant), ("includeDescendants", "true"), ("accessLevel", "full"))));
        Assert.True(await Count("SELECT COUNT(*) FROM auth.userAssignment WHERE appUserId=@u AND roleId=@r AND tenantId=@t AND isDeleted=0", ("@u", user), ("@r", roleId), ("@t", tenant)) == 1,
            "Grant refused: " + Excerpt(await actor.Http.GetStringAsync($"/admin/users/{user}")));
        await Refresh(user);
    }
    private async Task Profile(MeritClient client, string name, (Guid Tenant, string Role, string Access)[] grants)
    {
        string user = signedIn.First(p => ReferenceEquals(p.Value, client)).Key;
        await client.Http.GetStringAsync("/account/profiles/new");
        var values = new List<(string, object?)> { ("ProfileName", name), ("ProfileDescription", "Scenario 267 " + name), ("IsActive", "true") };
        for (int i = 0; i < grants.Length; i++)
            values.AddRange([($"Grants[{i}].TenantId", grants[i].Tenant), ($"Grants[{i}].RoleId", await Role(grants[i].Tenant, grants[i].Role)), ($"Grants[{i}].AccessLevel", grants[i].Access), ($"Grants[{i}].IncludeDescendants", "true")]);
        await Saved(await client.NoRedirect.PostAsync("/account/profiles", Form(values.ToArray())));
        Assert.Equal(1, await Count("SELECT COUNT(*) FROM auth.userProfile p JOIN auth.appUser u ON u.appUserId=p.appUserId WHERE u.userName=@u AND p.profileName=@n AND p.isActive=1", ("@u", user), ("@n", name)));
    }
    private static async Task<Guid> ProfileId(string user, string name) => (Guid)(await Sql("SELECT p.userProfileId FROM auth.userProfile p JOIN auth.appUser u ON u.appUserId=p.appUserId WHERE u.userName=@u AND p.profileName=@n", ("@u", user), ("@n", name)))!;
    private static async Task Switch(MeritClient client, Guid? profile)
    {
        await client.Http.GetStringAsync("/account/profiles");
        await Saved(await client.NoRedirect.PostAsync("/account/profiles/switch", Form(("userProfileId", profile?.ToString() ?? ""))));
    }
    private async Task Threshold(MeritClient actor, string minimum, string from, string? to)
    {
        await actor.Http.GetStringAsync("/config/thresholds/new");
        await Saved(await actor.NoRedirect.PostAsync("/config/thresholds", Form(("ThresholdId", Guid.NewGuid()), ("AnalyteSpecimenId", specimen), ("Population", "child"), ("AgeMaxMonths", 71),
            ("ThresholdType", "case"), ("LowerValue", minimum), ("Comparator", ">="), ("IsActive", "true"), ("EffectiveFrom", from), ("EffectiveTo", to ?? ""), ("Authority", "Synthetic scenario 267: Md. Environment § 6-801 reference level, not program approval"))));
    }
    private async Task<Guid> Template(MeritClient actor, string key)
    {
        string page = await actor.Http.GetStringAsync("/config/notification-rules?template=" + key);
        var draft = Merit.Application.Services.MarylandPolicyDrafts.Get(key);
        Assert.Equal(draft["ruleName"], WebUtility.HtmlDecode(Regex.Match(page, "id=\"ruleName\" name=\"ruleName\" value=\"([^\"]*)\"").Groups[1].Value));
        var id = Guid.NewGuid();
        var values = draft.Select(d => (d.Key, (object?)d.Value)).ToList();
        values.AddRange([("notificationRuleId", id), ("analyteSpecimenId", specimen), ("calendarName", "Synthetic weekdays only"), ("holidays", ""), ("approved", "true")]);
        await Saved(await actor.NoRedirect.PostAsync("/config/notification-rules", Form(values.ToArray())));
        Assert.Equal(1, await Count("SELECT COUNT(*) FROM config.notificationRule WHERE notificationRuleId=@id", ("@id", id)));
        return id;
    }
    private async Task<Guid> Format(MeritClient actor, string type, string[] fields)
    {
        await actor.Http.GetStringAsync("/config/intake-formats");
        var values = new List<(string Key, object? Value)> { ("SenderOrganizationId", lab), ("ProfileName", "Scenario 267 " + type), ("FileType", type), ("HeaderRow", 1), ("DateFormats", "yyyy-MM-dd") };
        values.AddRange(fields.Select(f => ("Columns[" + f + "]", (object?)f)));
        await Saved(await actor.NoRedirect.PostAsync("/config/intake-formats", Form(values.ToArray())));
        return (Guid)(await Sql("SELECT intakeProfileId FROM dbo.intakeProfile WHERE rootTenantId=@r AND profileName=@n AND isActive=1", ("@r", mdRoot), ("@n", "Scenario 267 " + type)))!;
    }
    private async Task<Guid> Person(MeritClient actor, string name, DateOnly dob)
    {
        await actor.Http.GetStringAsync("/people/new");
        await Saved(await actor.NoRedirect.PostAsync("/people/new", Form(("LastName", name), ("FirstName", "Ada"), ("Dob", dob.ToString("yyyy-MM-dd")), ("Sex", "female"), ("Ethnicity", "unknown"))));
        return (Guid)(await Sql("SELECT personId FROM dbo.person WHERE rootTenantId=@r AND lastName=@n AND isDeleted=0", ("@r", mdRoot), ("@n", name)))!;
    }
    private async Task Place(MeritClient actor, Guid person, string line1, string city, string zip, string county)
    {
        await actor.Http.GetStringAsync($"/people/{person}/places/add");
        await Saved(await actor.NoRedirect.PostAsync($"/people/{person}/places/add", Form(("Role", "residence"), ("AddressLine1", line1), ("City", city), ("State", "MD"), ("Zip5", zip), ("CountyFips", county),
            ("StartDate", today.AddYears(-3).ToString("yyyy-MM-dd")), ("Source", "interview"), ("PrimaryChoice", "new"), ("PrimaryReason", "mostOvernights"))));
    }
    private async Task<Guid> Manual(MeritClient actor, Guid patient, string value, string sample, DateOnly drawn, string accession)
    {
        string day = drawn.ToString("yyyy-MM-dd"), receipt = drawn.AddDays(2).ToString("yyyy-MM-dd");
        await actor.Http.GetStringAsync($"/people/{patient}/results/new");
        await Saved(await actor.NoRedirect.PostAsync($"/people/{patient}/results", Form(("AnalyteId", analyte), ("AnalyteSpecimenId", specimen), ("ValueText", value), ("Unit", "ug/dL"), ("SampleType", sample),
            ("CollectedDate", day), ("ReportedDate", receipt), ("FinalResultDate", receipt), ("DepartmentReceivedAt", receipt + "T10:00"), ("LabOrganizationId", lab), ("AccessionNumber", accession), ("ResultStatus", "final"))));
        return (Guid)(await Sql("SELECT labResultId FROM dbo.labResult WHERE rootTenantId=@r AND accessionNumber=@a", ("@r", mdRoot), ("@a", accession)))!;
    }
    private static async Task<HttpResponseMessage> PlanAsync(MeritClient user, Guid person, Guid id, string kind, Guid result, Guid? caseId = null, bool remediation = false)
    {
        await user.Http.GetStringAsync($"/people/{person}/care/testing");
        return await user.NoRedirect.PostAsync($"/people/{person}/care/testing", Form(("workTaskId", id), ("planKind", kind), ("labResultId", result), ("exposureCaseId", caseId),
            ("evidence", "Synthetic scenario 267: original reports and clinician-approved Maryland plan reviewed."), ("clinicianConfirmed", "true"), ("remediationConfirmed", remediation ? "true" : "false")));
    }
    private static async Task<Guid> PersonOf(Guid result) => (Guid)(await Sql("SELECT personId FROM dbo.labResult WHERE labResultId=@id", ("@id", result)))!;
    private static async Task<Guid> CaseOf(Guid result) => (Guid)(await Sql("SELECT TOP(1) exposureCaseId FROM dbo.exposureCase WHERE qualifyingLabResultId=@id OR personId=(SELECT personId FROM dbo.labResult WHERE labResultId=@id) ORDER BY createdDateUtc", ("@id", result)))!;
    private static async Task<Guid> CaseOfPerson(Guid person) => (Guid)(await Sql("SELECT TOP(1) exposureCaseId FROM dbo.exposureCase WHERE personId=@p AND caseStatus<>'closed' ORDER BY createdDateUtc", ("@p", person)))!;
    private static async Task<string> RowVersion(Guid caseId) => "0x" + Convert.ToHexString((byte[])(await Sql("SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id", ("@id", caseId)))!);
    private static async Task<string> InvestigationVersion(Guid id) => "0x" + Convert.ToHexString((byte[])(await Sql("SELECT rowVersion FROM dbo.investigation WHERE investigationId=@id", ("@id", id)))!);
    private async Task<List<Guid>> Investigations()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT investigationId FROM dbo.investigation WHERE rootTenantId=@r", c); cmd.Parameters.AddWithValue("@r", mdRoot);
        var list = new List<Guid>(); await using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) list.Add(reader.GetGuid(0)); return list;
    }
    private async Task<Guid> Upload(MeritClient actor, Guid user, Guid profile, string content, string filename, DateOnly receipt, int expectedLoaded = 1)
    {
        await actor.Http.GetStringAsync("/intake"); using var form = new MultipartFormDataContent();
        foreach (var (key, value) in new[] { ("intakeProfileId", profile.ToString()), ("historical", "true"), ("departmentReceivedAt", receipt.ToString("yyyy-MM-dd") + "T09:00"), ("receiptEvidence", "Synthetic receipt ledger scenario 267"), ("receiptConfirmed", "true") }) form.Add(new StringContent(value), key);
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", filename); await Saved(await actor.NoRedirect.PostAsync("/intake/upload", form));
        var batch = (Guid)(await Sql("SELECT TOP(1) intakeBatchId FROM dbo.intakeBatch WHERE rootTenantId=@r AND fileName=@n ORDER BY receivedUtc DESC", ("@r", mdRoot), ("@n", filename)))!;
        string json = (string)(await Sql("SELECT TOP(1) * FROM dbo.jobQueue WHERE JSON_VALUE(payloadJson,'$.intakeBatchId')=CONVERT(VARCHAR(36),@id) FOR JSON PATH,WITHOUT_ARRAY_WRAPPER", ("@id", batch)))!;
        var job = JsonSerializer.Deserialize<ClaimedJobRow>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var session = new ReplaySession(); var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ICurrentSession>(session);
        services.AddMeritApplication(); services.AddMeritSqlServer(o => o.ConnectionString = TestEnvironment.WorkerConnectionString);
        await using var provider = services.BuildServiceProvider();
        session.Current = await JobSessions.OpenAsync(provider.GetRequiredService<IAccountData>(), job, DateTime.UtcNow.AddHours(1));
        TestSessionScopes.Track(session.Current.Summary.SessionScopeId);
        var result = await provider.GetRequiredService<JobDispatcher>().RunAsync(job, default); await provider.GetRequiredService<IWorkData>().CompleteJobAsync(job.JobId, result);
        Assert.Equal(expectedLoaded, await Count("SELECT loadedCount FROM dbo.intakeBatch WHERE intakeBatchId=@id", ("@id", batch)));
        return (Guid)(await Sql("SELECT TOP(1) labResultId FROM dbo.labResult WHERE intakeBatchId=@id AND resultRowStatus='valid' ORDER BY createdDateUtc", ("@id", batch)))!;
    }
    private string Hl7(string name, string value, string county, string city, string zip, DateOnly drawn, DateOnly final, int ageMonths = 30)
        => $"MSH|^~\\&|LAB|{clia}|MERIT|MD|{final:yyyyMMdd}||ORU^R01^ORU_R01|{name}|P|2.5.1\rPID|1||{name}^^^LAB^MR||{name}^Ada||{drawn.AddMonths(-ageMonths):yyyyMMdd}|F|||9900 Synthetic Way^^{city}^MD^{zip}^USA^H^^24{county}||4105550249\rNK1|1|Guardian{name}^Grace||20 Caregiver St^^{city}^MD^{zip}|4105550199\rOBR|1||{name}||||{drawn:yyyyMMdd}|||||||||||||||{final:yyyyMMdd}|||F\rOBX|1|NM|77307-7^Lead^LN|reading-1|{value}|ug/dL|||||F\r";

    private sealed class ReplaySession : ICurrentSession
    {
        public SessionScopeResult? Current { get; set; }
        public bool IsAuthenticated => Current is not null;
        public Guid? UserId => Current?.Summary.AppUserId;
        public string? UserName => Current?.Summary.UserName;
        public string? DisplayName => Current?.Summary.DisplayName;
        public Guid? ScopeKey => Current?.Summary.SessionScopeId;
        public Guid? RootTenantId => Current?.Summary.RootTenantId;
        public Guid? ActingTenantId => Current?.Summary.ActingTenantId;
        public string? ActingTenantName => Current?.Summary.ActingTenantName;
        public IReadOnlySet<string> Permissions => Current?.Permissions.ToHashSet(StringComparer.Ordinal) ?? [];
    }
}
