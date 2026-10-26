using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>Issue #20: the same file uploaded twice for the same sender is refused unless the user says it is a re-send.</summary>
[Collection(MeritCollection.Name)]
public sealed partial class IntakeUploadTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task TheSameFileIsNotProcessedTwiceByMistake()
    {
        var supervisor = await fixture.SignedInAsync("supervisor");
        Guid profile;
        await using (var c = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT TOP (1) intakeProfileId FROM dbo.intakeProfile WHERE isActive = 1 AND isDeleted = 0", c);
            profile = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        // Content of its own, so no earlier run's batch has the same fingerprint.
        string name = $"upload-twice-{Guid.NewGuid():N}.csv";
        string content = "PatientLast,PatientFirst\r\nTwice," + Guid.NewGuid().ToString("N") + "\r\n";
        try
        {
            Assert.Contains("File received", await UploadAsync(supervisor, profile, name, content, uploadAgain: false), StringComparison.Ordinal);

            // Again: refused, with the date and a link to the batch that has it.
            string again = await UploadAsync(supervisor, profile, name, content, uploadAgain: false);
            Assert.Contains("This file was already received on", again, StringComparison.Ordinal);
            Assert.Contains("It was not processed again.", again, StringComparison.Ordinal);
            Assert.Matches("href=\"#batch-[0-9a-f-]{36}\">Show that batch", again);
            Assert.Contains("name=\"uploadAgain\"", again, StringComparison.Ordinal);

            // A genuine re-send, said so explicitly, is received.
            Assert.Contains("File received", await UploadAsync(supervisor, profile, name, content, uploadAgain: true), StringComparison.Ordinal);
            Assert.Equal(2, await BatchCountAsync(name));
        }
        finally
        {
            await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
            await c.OpenAsync();
            await using var cmd = new SqlCommand("""
                DECLARE @b TABLE (intakeBatchId UNIQUEIDENTIFIER);
                INSERT @b SELECT intakeBatchId FROM dbo.intakeBatch WHERE fileName = @name;
                DELETE j FROM dbo.jobQueue AS j JOIN @b AS b ON j.payloadJson LIKE CONCAT (N'%', b.intakeBatchId, N'%');
                DELETE dbo.intakeBatch WHERE intakeBatchId IN (SELECT intakeBatchId FROM @b);
                """, c);
            cmd.Parameters.AddWithValue("@name", name);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Posts the upload form as the browser does and returns the page it lands on.</summary>
    private static async Task<string> UploadAsync(MeritClient client, Guid profile, string fileName, string content, bool uploadAgain)
    {
        string page = await client.Http.GetStringAsync("/intake");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(TokenPattern().Match(page).Groups[1].Value), "__RequestVerificationToken" },
            { new StringContent(profile.ToString()), "intakeProfileId" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName },
        };
        if (uploadAgain)
        {
            form.Add(new StringContent("true"), "uploadAgain");
        }

        var response = await client.Http.PostAsync("/intake/upload", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task<int> BatchCountAsync(string fileName)
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT (*) FROM dbo.intakeBatch WHERE fileName = @name AND isDeleted = 0", c);
        cmd.Parameters.AddWithValue("@name", fileName);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
