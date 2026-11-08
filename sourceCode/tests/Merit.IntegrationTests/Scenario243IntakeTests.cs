namespace Merit.IntegrationTests;

public sealed partial class Hl7IntakeTests
{
    [SqlTheory]
    [InlineData(2020, "hl7")]
    [InlineData(2025, "csv")]
    public async Task Scenario243HistoricalImportsPreserveDrawButUseActualUploadReceipt(int year, string type)
    {
        _ = fixture.Ids;
        await using var harness = await Harness.OpenAsync();
        var profile = await harness.ProfileAsync(type);
        string name = "Scenario243" + Guid.NewGuid().ToString("N");
        string content = type == "hl7"
            ? Message(name, name, dob: "20180102").Replace("20261020", "20200801").Replace("20261022", "20200803")
            : $"lastName,firstName,dob,sex,loinc,value,unit,collectedDate,reportedDate,accessionNumber\r\n{name},Ada{name},2023-01-02,female,77307-7,4,ug/dL,2025-03-01,2025-03-03,{name}\r\n";
        var batch = await harness.RunAsync(profile, content);
        Assert.Equal(1, batch.LoadedCount);
        Assert.Equal(new DateTime(year, year == 2020 ? 8 : 3, 1),
            await SqlAsync<DateTime>("SELECT collectedDate FROM dbo.labResult WHERE intakeBatchId=@a AND resultRowStatus='valid'", batch.IntakeBatchId));
        // This is an observed replay limitation, not an assertion that upload time is the correct historical receipt.
        var received = await SqlAsync<DateTime>("SELECT receivedUtc FROM dbo.labResult WHERE intakeBatchId=@a AND resultRowStatus='valid'", batch.IntakeBatchId);
        Assert.InRange(Math.Abs((received - batch.ReceivedUtc).TotalMilliseconds), 0, 10); // SQL DATETIME precision.
        Assert.NotEqual(year, batch.ReceivedUtc.Year);
    }
}
