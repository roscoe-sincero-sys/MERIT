using System.Net;
using System.Text;
using System.Text.Json;
using Merit.Domain.Text;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// Regression coverage for place-role input resolution (issue #62) and match-review last-candidate rejection (issue #63).
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class PlaceAndMatchReviewTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task PlaceSubmissionResolvesLocationStrictlyByChosenRole()
    {
        // Issue #62: a hidden OrganizationSite left over from Child care must not override a typed Residence address,
        // and a Child-care submission without an OrganizationSite must not fall back to hidden Residence address fields.
        string tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var personId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var siteLocId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        string residenceStreet = $"742 Evergreen {tag} St";

        try
        {
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await using var cmd = new SqlCommand("""
                    DECLARE @md UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-LEAD');
                    INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey, dob)
                    VALUES (@person, @md, @md, @last, N'Noah', @lastKey, N'NOAH', '2021-03-15');
                    INSERT dbo.location (locationId, rootTenantId, owningTenantId, addressLine1, city, state, zip5, addressKey)
                    VALUES (@siteLoc, @md, @md, N'100 Provider Way', N'Baltimore', 'MD', '21202', @siteKey);
                    INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey)
                    VALUES (@org, @md, @md, @orgName, UPPER (@orgName));
                    INSERT dbo.organizationSite (organizationSiteId, rootTenantId, owningTenantId, organizationId, locationId, siteName)
                    VALUES (@site, @md, @md, @org, @siteLoc, N'Main Campus');
                    """, c);
                cmd.Parameters.AddWithValue("@person", personId);
                cmd.Parameters.AddWithValue("@last", "Place" + tag);
                cmd.Parameters.AddWithValue("@lastKey", MatchKeys.NameKey("Place" + tag)!);
                cmd.Parameters.AddWithValue("@siteLoc", siteLocId);
                cmd.Parameters.AddWithValue("@siteKey", "SITE-" + tag);
                cmd.Parameters.AddWithValue("@org", orgId);
                cmd.Parameters.AddWithValue("@orgName", "Sunshine Child Care " + tag);
                cmd.Parameters.AddWithValue("@site", siteId);
                await cmd.ExecuteNonQueryAsync();
            }

            var supervisor = await fixture.SignedInAsync("supervisor");
            await supervisor.Http.GetStringAsync("/"); // ensures XSRF-TOKEN cookie is present

            // 1. Posting Role=residence alongside a stale OrganizationSite uses the visible residence address, not the site.
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"/people/{personId}/places/add"))
            {
                req.Headers.Add("HX-Request", "true");
                req.Headers.Add("HX-Request-Type", "partial");
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Role"] = "residence",
                    ["StartDate"] = "2026-01-15",
                    ["OrganizationSite"] = $"{siteId}:{siteLocId}",
                    ["OrganizationLabel"] = "Sunshine Child Care " + tag,
                    ["AddressLine1"] = residenceStreet,
                    ["City"] = "Baltimore",
                    ["State"] = "MD",
                    ["Zip5"] = "21201",
                    ["Source"] = "interview",
                });
                using var res = await supervisor.NoRedirect.SendAsync(req);
                Assert.True(res.StatusCode == HttpStatusCode.OK, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
            }

            Guid savedResidenceLocationId;
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await using var cmd = new SqlCommand("""
                    SELECT pl.locationId, pl.organizationSiteId, l.addressLine1
                      FROM dbo.personLocation AS pl
                      JOIN dbo.location AS l ON l.locationId = pl.locationId
                     WHERE pl.personId = @person AND pl.isDeleted = 0;
                    """, c);
                cmd.Parameters.AddWithValue("@person", personId);
                await using var r = await cmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                savedResidenceLocationId = r.GetGuid(0);
                Assert.NotEqual(siteLocId, savedResidenceLocationId);
                Assert.True(r.IsDBNull(1));
                Assert.Equal(residenceStreet, r.GetString(2));
                Assert.False(await r.ReadAsync());
            }

            // 2. Posting Role=childCare without OrganizationSite refuses with an OrganizationSite error even if hidden
            //    residence fields (LocationId / AddressLine1) were submitted.
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"/people/{personId}/places/add"))
            {
                req.Headers.Add("HX-Request", "true");
                req.Headers.Add("HX-Request-Type", "partial");
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Role"] = "childCare",
                    ["StartDate"] = "2026-02-01",
                    ["LocationId"] = savedResidenceLocationId.ToString(),
                    ["AddressLine1"] = "999 Hidden Residence Rd",
                    ["City"] = "Baltimore",
                    ["State"] = "MD",
                    ["Zip5"] = "21201",
                    ["Source"] = "interview",
                });
                using var res = await supervisor.NoRedirect.SendAsync(req);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
                string html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
                Assert.Contains("Choose a child-care provider or school.", html, StringComparison.Ordinal);
            }
        }
        finally
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
            await c.OpenAsync();
            await using var cmd = new SqlCommand("""
                DECLARE @locs TABLE (locationId UNIQUEIDENTIFIER);
                INSERT @locs SELECT locationId FROM dbo.personLocation WHERE personId = @person;
                DELETE dbo.personLocation WHERE personId = @person;
                DELETE dbo.organizationSite WHERE organizationId = @org;
                DELETE dbo.organization WHERE organizationId = @org;
                DELETE dbo.person WHERE personId = @person;
                DELETE dbo.location WHERE locationId = @siteLoc OR locationId IN (SELECT locationId FROM @locs);
                """, c);
            cmd.Parameters.AddWithValue("@person", personId);
            cmd.Parameters.AddWithValue("@org", orgId);
            cmd.Parameters.AddWithValue("@siteLoc", siteLocId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [SqlFact]
    public async Task MarkingTheLastCandidateDifferentKeepsTheRowHeldForExplicitNewPersonChoice()
    {
        // Issue #63: rejecting the final candidate with "different" must record the exclusion and leave the record held
        // in review with 0 candidates rather than automatically creating and releasing a new person.
        string tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string lastName = "Uxmatch" + tag;
        string lastKey = MatchKeys.NameKey(lastName)!;
        var candidateId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var rowId = Guid.NewGuid();

        try
        {
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await using var cmd = new SqlCommand("""
                    DECLARE @md UNIQUEIDENTIFIER = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-LEAD');
                    INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey, dob, sex)
                    VALUES (@candidate, @md, @md, @last, N'Maya', @lastKey, N'MAYA', '2021-05-14', 'female');
                    INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey)
                    VALUES (@sender, @md, @md, @senderName, UPPER (@senderName));
                    INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, senderOrganizationId, sourceChannel, fileName,
                                            receivedUtc, batchStatus)
                    VALUES (@batch, @md, @md, @sender, 'upload', N'ux63-match.csv', SYSUTCDATETIME (), 'loaded');
                    INSERT dbo.stagingRow (stagingRowId, rootTenantId, owningTenantId, intakeBatchId, rowNumber, parsedJson, rowStatus,
                                           isUrgent, heldSinceUtc)
                    VALUES (@row, @md, @md, @batch, 1, @parsed, 'held', 0, SYSUTCDATETIME ());
                    INSERT dbo.stagingHoldReason (stagingHoldReasonId, rootTenantId, owningTenantId, stagingRowId, reasonCode, reasonMessage)
                    VALUES (NEWID (), @md, @md, @row, 'uncertainMatch', N'Close name and matching date of birth.');
                    """, c);
                cmd.Parameters.AddWithValue("@candidate", candidateId);
                cmd.Parameters.AddWithValue("@last", lastName);
                cmd.Parameters.AddWithValue("@lastKey", lastKey);
                cmd.Parameters.AddWithValue("@sender", senderId);
                cmd.Parameters.AddWithValue("@senderName", "Lab UX63 " + tag);
                cmd.Parameters.AddWithValue("@batch", batchId);
                cmd.Parameters.AddWithValue("@row", rowId);
                cmd.Parameters.AddWithValue("@parsed",
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["lastName"] = lastName,
                        ["firstName"] = "Maia",
                        ["dob"] = "2021-05-14",
                        ["sex"] = "female",
                        ["loinc"] = "77307-7",
                        ["value"] = "4.2",
                        ["unit"] = "ug/dL",
                    }));
                await cmd.ExecuteNonQueryAsync();
            }

            var supervisor = await fixture.SignedInAsync("supervisor");
            await supervisor.Http.GetStringAsync("/");

            using (var beforeRes = await supervisor.NoRedirect.GetAsync($"/api/v1/match-review/{rowId}"))
            {
                Assert.Equal(HttpStatusCode.OK, beforeRes.StatusCode);
                using var before = JsonDocument.Parse(await beforeRes.Content.ReadAsStringAsync());
                var cand = Assert.Single(before.RootElement.GetProperty("candidates").EnumerateArray());
                Assert.Equal(candidateId, cand.GetProperty("personId").GetGuid());
            }

            using (var decideRes = await supervisor.NoRedirect.PostAsync($"/api/v1/match-review/{rowId}/decision",
                       new StringContent(JsonSerializer.Serialize(new { decision = "different", personId = candidateId, note = (string?)null }),
                           Encoding.UTF8, "application/json")))
            {
                Assert.True(decideRes.StatusCode == HttpStatusCode.OK, $"{(int)decideRes.StatusCode}: {await decideRes.Content.ReadAsStringAsync()}");
                using var outcome = JsonDocument.Parse(await decideRes.Content.ReadAsStringAsync());
                Assert.False(outcome.RootElement.GetProperty("released").GetBoolean());
                Assert.Equal(0, outcome.RootElement.GetProperty("remainingCandidates").GetInt32());
            }

            // The row remains held without matchNewPerson, and reloading it shows 0 candidates plus the incoming identity.
            await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await c.OpenAsync();
                await using var cmd = new SqlCommand("""
                    SELECT rowStatus, COALESCE (correctedJson, N''),
                           (SELECT COUNT (*) FROM dbo.matchExclusion WHERE stagingRowId = @row AND personId = @candidate AND isDeleted = 0)
                      FROM dbo.stagingRow
                     WHERE stagingRowId = @row;
                    """, c);
                cmd.Parameters.AddWithValue("@row", rowId);
                cmd.Parameters.AddWithValue("@candidate", candidateId);
                await using var r = await cmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                Assert.Equal("held", r.GetString(0));
                Assert.DoesNotContain("matchNewPerson", r.GetString(1), StringComparison.Ordinal);
                Assert.Equal(1, r.GetInt32(2));
            }

            using (var afterRes = await supervisor.NoRedirect.GetAsync($"/api/v1/match-review/{rowId}"))
            {
                Assert.Equal(HttpStatusCode.OK, afterRes.StatusCode);
                using var after = JsonDocument.Parse(await afterRes.Content.ReadAsStringAsync());
                Assert.Empty(after.RootElement.GetProperty("candidates").EnumerateArray());
                var incomingKeys = after.RootElement.GetProperty("incoming").EnumerateArray()
                    .ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").GetString());
                Assert.Equal(lastName, incomingKeys["lastName"]);
                Assert.Equal("Maia", incomingKeys["firstName"]);
                Assert.Equal("2021-05-14", incomingKeys["dob"]);
            }
        }
        finally
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
            await c.OpenAsync();
            await using var cmd = new SqlCommand("""
                DELETE dbo.matchExclusion WHERE stagingRowId = @row OR personId = @candidate;
                DELETE dbo.workTask WHERE stagingRowId = @row;
                DELETE dbo.stagingHoldReason WHERE stagingRowId = @row;
                DELETE j FROM dbo.jobQueue AS j WHERE j.payloadJson LIKE CONCAT (N'%', @row, N'%');
                DELETE dbo.stagingRow WHERE stagingRowId = @row;
                DELETE dbo.intakeBatch WHERE intakeBatchId = @batch;
                DELETE dbo.organization WHERE organizationId = @sender;
                DELETE dbo.person WHERE personId = @candidate;
                """, c);
            cmd.Parameters.AddWithValue("@row", rowId);
            cmd.Parameters.AddWithValue("@batch", batchId);
            cmd.Parameters.AddWithValue("@sender", senderId);
            cmd.Parameters.AddWithValue("@candidate", candidateId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
