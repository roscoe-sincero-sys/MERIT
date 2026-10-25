using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace Merit.IntegrationTests;

/// <summary>
/// A Baltimore County case manager must see and change nothing that belongs only to Baltimore City or the state program:
/// through the screens and API, and through every stored procedure the web login can execute.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class CrossTenantHttpTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task AnotherCountysRecordsAreNotFound()
    {
        var county = await fixture.SignedInAsync("cmbaltco");
        var ids = fixture.Ids;
        foreach (var url in new[]
                 {
                     $"/cases/{ids.MiaCaseId}", $"/people/{ids.MiaPersonId}", $"/api/v1/people/{ids.MiaPersonId}/timeline",
                     $"/results/{ids.MiaLabResultId}", $"/people/{ids.MiaPersonId}/places",
                 })
        {
            var response = await county.Http.GetAsync(url);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
                $"{url} answered {(int)response.StatusCode} for another county's case manager.");
        }

        if (ids.ClusterId is { } cluster)
        {
            var response = await county.NoRedirect.GetAsync($"/api/v1/clusters/{cluster}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [SqlFact]
    public async Task ListsLeaveOutAnotherCountysRecords()
    {
        var county = await fixture.SignedInAsync("cmbaltco");
        string mia = fixture.Ids.MiaPersonId.ToString();
        foreach (var url in new[] { "/api/v1/results?pageSize=500", "/api/v1/tasks?status=&pageSize=500", "/api/v1/cases/map?openOnly=false", "/cases" })
        {
            var body = await county.Http.GetStringAsync(url);
            Assert.DoesNotContain(mia, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fixture.Ids.MiaCaseNumber, body, StringComparison.Ordinal);
        }

        var city = await fixture.SignedInAsync("cmbalt");
        Assert.Contains(fixture.Ids.MiaCaseNumber, await city.Http.GetStringAsync("/cases"), StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task AnotherCountysCaseCannotBeChanged()
    {
        var county = await fixture.SignedInAsync("cmbaltco");
        var response = await county.NoRedirect.PostAsync($"/api/v1/clusters/{fixture.Ids.ClusterId ?? Guid.NewGuid()}/members",
            new StringContent(JsonSerializer.Serialize(new { caseNumber = fixture.Ids.MiaCaseNumber, isMember = false }), Encoding.UTF8, "application/json"));
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"Cluster change answered {(int)response.StatusCode}.");
    }
}

/// <summary>
/// The procedure-level suite (main specification, "an automated test suite attempts cross-tenant reads and writes through
/// every procedure"): for each procedure granted to meritWebRole, called as the web login under a Baltimore County
/// session with every id parameter pointing at a Baltimore City or state-owned record, no result set may contain an id
/// of a record outside the caller's scope, and no such record may change. With no session at all, nothing tenant-owned
/// comes back.
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class CrossTenantProcedureTests(MeritFixture fixture, ITestOutputHelper output)
{
    private sealed record ManifestParam(string Name, string Type, bool HasDefault, bool IsOutput);

    private sealed record ManifestProc(string Name, string Kind, string[] Grants, string? Permission, ManifestParam[] Params);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Session plumbing and job procedures: calling them proves nothing about tenant data, and some end the session.
    private static readonly HashSet<string> NotTenantData = new(StringComparer.Ordinal)
    {
        "auth.uspCreateSessionScope", "auth.uspRevokeSessionScope", "auth.uspGetSessionScope", "auth.uspGetUserForSignIn",
        "auth.uspRecordSignInResult", "auth.uspSaveMfaEnrollment", "auth.uspUseRecoveryCode", "auth.uspGetRecoveryCodes",
    };

    private static IReadOnlyList<ManifestProc> WebProcedures()
        => JsonSerializer.Deserialize<ManifestProc[]>(File.ReadAllText(Path.Combine(TestEnvironment.SourceRoot, "database", "procedureManifest.json")), Json)!
            .Where(p => p.Grants.Contains("meritWebRole") && !NotTenantData.Contains(p.Name))
            .ToList();

    [SqlFact]
    public async Task NoProcedureLeaksOrChangesAnotherTenantsRows()
    {
        var caller = await UserIdAsync("cmbaltco");
        var scope = await NewScopeAsync(caller);
        var visible = await VisibleTenantsAsync(scope);
        var targets = await TargetsAsync(visible);
        var secrets = await SecretIdsAsync(visible);
        var before = await SnapshotAsync(visible, targets.Persons);
        Assert.NotEmpty(secrets);

        // Positive control: the harness itself works, so a refusal below means the procedure refused.
        var own = await ExecuteAsync(await NewScopeAsync(caller), "dbo.uspListAssignableUsers", []);
        Assert.NotEmpty(own);

        var leaks = new List<string>();
        int calls = 0, refused = 0;
        foreach (var proc in WebProcedures())
        {
            var (values, sql) = Call(proc, targets);
            try
            {
                var cells = await ExecuteAsync(await NewScopeAsync(caller), sql, values);
                calls++;
                var passed = values.Values.Select(v => v?.ToString()).Where(v => v is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var cell in cells.Where(c => secrets.Contains(c) && !passed.Contains(c.ToString())))
                {
                    leaks.Add($"{proc.Name} returned {cell}, a record outside the caller's tenants.");
                }
            }
            catch (SqlException)
            {
                // Refused: not found, permission, validation or a block predicate. Any of these is the safe outcome.
                calls++;
                refused++;
            }
        }

        var after = await SnapshotAsync(visible, targets.Persons);
        var changed = before.Where(b => after.TryGetValue(b.Key, out var a) && a != b.Value).Select(b => b.Key).ToList();
        output.WriteLine($"{calls} procedure calls, {refused} refused, {secrets.Count} out-of-scope ids watched.");
        Assert.True(calls - refused >= calls / 3, $"Only {calls - refused} of {calls} calls ran; the suite is not exercising the procedures.");
        Assert.True(leaks.Count == 0, string.Join('\n', leaks.Distinct()));
        Assert.True(changed.Count == 0, "Out-of-scope rows changed in: " + string.Join(", ", changed));
    }

    /// <summary>Self-check: the same scan, run as the statewide supervisor, does find county-hidden ids, so it can see a leak.</summary>
    [SqlFact]
    public async Task TheScanWouldNoticeALeak()
    {
        var countyScope = await NewScopeAsync(await UserIdAsync("cmbaltco"));
        var secrets = await SecretIdsAsync(await VisibleTenantsAsync(countyScope));
        var statewide = await ExecuteAsync(await NewScopeAsync(await UserIdAsync("supervisor")), "dbo.uspListExposureCases",
            new Dictionary<string, object?> { ["openOnly"] = false, ["pageSize"] = 100 });
        Assert.Contains(statewide, secrets.Contains);
    }

    [SqlFact]
    public async Task WithoutASessionNothingTenantOwnedComesBack()
    {
        var allIds = await SecretIdsAsync([]);
        var leaks = new List<string>();
        foreach (var proc in WebProcedures().Where(p => p.Kind == "read"))
        {
            var (values, sql) = Call(proc, new Targets(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [], []));
            try
            {
                foreach (var cell in (await ExecuteAsync(null, sql, values)).Where(allIds.Contains))
                {
                    leaks.Add($"{proc.Name} returned {cell} with no session.");
                }
            }
            catch (SqlException)
            {
                // Fail closed by refusing is also fine.
            }
        }

        Assert.True(leaks.Count == 0, string.Join('\n', leaks.Distinct()));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private sealed record Targets(Guid Person, Guid Case, Guid Result, Guid Investigation, Guid Cluster, Guid StagingRow, Guid Batch,
                                  Guid PersonLocation, IReadOnlyList<Guid> Tasks, IReadOnlyList<Guid> Persons);

    /// <summary>Parameter values: ids of out-of-scope records where the name says what the id is, harmless values otherwise.</summary>
    private static (Dictionary<string, object?> Values, string Sql) Call(ManifestProc proc, Targets t)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in proc.Params.Where(p => !p.IsOutput))
        {
            object? v = p.Name switch
            {
                "personId" or "relatedPersonId" or "contactPersonId" or "survivorPersonId" => t.Person,
                "exposureCaseId" => t.Case,
                "labResultId" or "qualifyingLabResultId" => t.Result,
                "investigationId" => t.Investigation,
                "clusterId" => t.Cluster,
                "stagingRowId" => t.StagingRow,
                "intakeBatchId" => t.Batch,
                "personLocationId" => t.PersonLocation,
                "stagingRowIdsJson" => JsonSerializer.Serialize(new[] { t.StagingRow }),
                "taskIdsJson" or "workTaskIdsJson" => JsonSerializer.Serialize(t.Tasks),
                "personIdsJson" => JsonSerializer.Serialize(t.Persons),
                "caseNumber" => null,
                _ when p.HasDefault => Skip,
                _ => Default(p),
            };
            if (!ReferenceEquals(v, Skip))
            {
                values[p.Name] = v;
            }
        }

        return (values, proc.Name);
    }

    private static readonly object Skip = new();

    private static object? Default(ManifestParam p)
    {
        string type = p.Type.ToUpperInvariant();
        if (type.StartsWith("UNIQUEIDENTIFIER", StringComparison.Ordinal)) return Guid.NewGuid();
        if (type.StartsWith("BINARY", StringComparison.Ordinal) || type.StartsWith("VARBINARY", StringComparison.Ordinal)) return new byte[8];
        if (type.StartsWith("BIT", StringComparison.Ordinal)) return false;
        if (type.StartsWith("INT", StringComparison.Ordinal) || type.StartsWith("SMALLINT", StringComparison.Ordinal)) return 1;
        if (type.StartsWith("DECIMAL", StringComparison.Ordinal)) return 1m;
        if (type.StartsWith("DATETIME", StringComparison.Ordinal)) return DateTime.UtcNow;
        if (type.StartsWith("DATE", StringComparison.Ordinal)) return p.Name.Contains("to", StringComparison.OrdinalIgnoreCase) ? DateTime.Today : DateTime.Today.AddYears(-1);
        if (p.Name.EndsWith("Json", StringComparison.Ordinal)) return p.Name.EndsWith("sJson", StringComparison.Ordinal) ? "[]" : "{}";
        return "x";
    }

    private static async Task<List<object>> ExecuteAsync(Guid? scope, string procedure, Dictionary<string, object?> values)
    {
        await using var c = new SqlConnection(TestEnvironment.WebConnectionString);
        await c.OpenAsync();
        if (scope is { } s)
        {
            await SetContextAsync(c, s);
        }

        await using var cmd = new SqlCommand(procedure, c) { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        foreach (var (k, v) in values)
        {
            cmd.Parameters.AddWithValue("@" + k, v ?? DBNull.Value);
        }

        var cells = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync();
        do
        {
            while (await r.ReadAsync())
            {
                for (int i = 0; i < r.FieldCount; i++)
                {
                    if (!r.IsDBNull(i) && r.GetValue(i) is Guid g)
                    {
                        cells.Add(g);
                    }
                }
            }
        }
        while (await r.NextResultAsync());
        return cells;
    }

    private static async Task SetContextAsync(SqlConnection c, Guid scope)
    {
        await using var ctx = new SqlCommand("""
            EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @scope, @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'appUserId', @value = @user, @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'AppUser', @value = N'cross-tenant suite', @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'actingTenantId', @value = @acting, @read_only = 1;
            """, c);
        ctx.Parameters.AddWithValue("@scope", scope);
        ctx.Parameters.AddWithValue("@user", (object?)CallerId ?? DBNull.Value);
        ctx.Parameters.AddWithValue("@acting", (object?)ActingTenant ?? DBNull.Value);
        await ctx.ExecuteNonQueryAsync();
    }

    private static Guid? ActingTenant { get; set; }

    private static Guid? CallerId { get; set; }

    /// <summary>A session scope exactly as sign-in creates one, through auth.uspCreateSessionScope as the web login.</summary>
    private async Task<Guid> NewScopeAsync(Guid appUserId)
    {
        ActingTenant = fixture.Ids.BaltimoreCountyTenantId;
        CallerId = appUserId;
        var scope = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.WebConnectionString);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("auth.uspCreateSessionScope", c) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@sessionScopeId", scope);
        cmd.Parameters.AddWithValue("@appUserId", appUserId);
        cmd.Parameters.AddWithValue("@expiresUtc", DateTime.UtcNow.AddMinutes(10));
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.NextResultAsync())
        {
        }

        return scope;
    }

    private static async Task<SqlConnection> AdminAsync()
    {
        var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        return c;
    }

    private static async Task<Guid> UserIdAsync(string userName)
    {
        await using var c = await AdminAsync();
        await using var cmd = new SqlCommand("SELECT appUserId FROM auth.appUser WHERE userName = @u", c);
        cmd.Parameters.AddWithValue("@u", userName);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<HashSet<Guid>> VisibleTenantsAsync(Guid scope)
    {
        await using var c = await AdminAsync();
        await using var cmd = new SqlCommand("SELECT tenantId FROM auth.sessionScopeTenant WHERE sessionScopeId = @s AND isDeleted = 0", c);
        cmd.Parameters.AddWithValue("@s", scope);
        var set = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            set.Add(r.GetGuid(0));
        }

        return set;
    }

    private static string InList(IReadOnlyCollection<Guid> ids)
        => ids.Count == 0 ? "'00000000-0000-0000-0000-000000000000'" : string.Join(',', ids.Select(i => $"'{i}'"));

    /// <summary>Out-of-scope records to aim every id parameter at.</summary>
    private static async Task<Targets> TargetsAsync(HashSet<Guid> visible)
    {
        string notMine = $"NOT IN ({InList(visible)})";
        string hiddenPerson = $"""
            p.owningTenantId {notMine}
            AND NOT EXISTS (SELECT 1 FROM dbo.personAccess AS pa WHERE pa.personId = p.personId AND pa.isDeleted = 0
                                                                    AND pa.tenantId IN ({InList(visible)}))
            """;
        await using var c = await AdminAsync();
        async Task<Guid> One(string sql)
        {
            await using var cmd = new SqlCommand(sql, c);
            return await cmd.ExecuteScalarAsync() is Guid g ? g : Guid.NewGuid();
        }

        async Task<List<Guid>> Many(string sql)
        {
            await using var cmd = new SqlCommand(sql, c);
            var list = new List<Guid>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(r.GetGuid(0));
            }

            return list;
        }

        var persons = await Many($"SELECT p.personId FROM dbo.person AS p WHERE {hiddenPerson}");
        return new Targets(
            persons.FirstOrDefault(),
            await One($"SELECT TOP (1) exposureCaseId FROM dbo.exposureCase WHERE owningTenantId {notMine} ORDER BY openedDate"),
            await One($"SELECT TOP (1) r.labResultId FROM dbo.labResult AS r JOIN dbo.person AS p ON p.personId = r.personId WHERE {hiddenPerson}"),
            await One($"SELECT TOP (1) investigationId FROM dbo.investigation WHERE owningTenantId {notMine}"),
            await One($"SELECT TOP (1) clusterId FROM dbo.cluster WHERE owningTenantId {notMine}"),
            await One($"SELECT TOP (1) stagingRowId FROM dbo.stagingRow WHERE owningTenantId {notMine} ORDER BY CASE rowStatus WHEN 'held' THEN 0 ELSE 1 END"),
            await One($"SELECT TOP (1) intakeBatchId FROM dbo.intakeBatch WHERE owningTenantId {notMine}"),
            await One($"SELECT TOP (1) pl.personLocationId FROM dbo.personLocation AS pl JOIN dbo.person AS p ON p.personId = pl.personId WHERE {hiddenPerson}"),
            await Many($"SELECT TOP (20) workTaskId FROM dbo.workTask WHERE owningTenantId {notMine}"),
            persons);
    }

    /// <summary>Every primary key of a row-level-security table that the caller must not see.</summary>
    private static async Task<HashSet<object>> SecretIdsAsync(HashSet<Guid> visible)
    {
        await using var c = await AdminAsync();
        var secrets = new HashSet<object>();
        foreach (var (table, pk, isPerson) in await PolicyTablesAsync(c))
        {
            string where = isPerson
                ? $"""
                   t.owningTenantId NOT IN ({InList(visible)}) AND NOT EXISTS (SELECT 1 FROM dbo.personAccess AS pa
                   WHERE pa.personId = t.personId AND pa.isDeleted = 0 AND pa.tenantId IN ({InList(visible)}))
                   """
                : $"t.owningTenantId NOT IN ({InList(visible)})";
            await using var cmd = new SqlCommand($"SELECT t.{pk} FROM {table} AS t WHERE {where}", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                secrets.Add(r.GetGuid(0));
            }
        }

        return secrets;
    }

    /// <summary>Row count and checksum of the out-of-scope rows of every protected table, to detect any change.</summary>
    private static async Task<Dictionary<string, string>> SnapshotAsync(HashSet<Guid> visible, IReadOnlyList<Guid> hiddenPersons)
    {
        await using var c = await AdminAsync();
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (table, pk, isPerson) in await PolicyTablesAsync(c))
        {
            string where = isPerson ? $"t.personId IN ({InList(hiddenPersons)})" : $"t.owningTenantId NOT IN ({InList(visible)})";
            await using var cmd = new SqlCommand(
                $"SELECT CONCAT (COUNT (*), ':', CHECKSUM_AGG (CHECKSUM (t.{pk}, t.isDeleted, t.modifiedDateUtc))) FROM {table} AS t WHERE {where}", c);
            snapshot[table] = (string)(await cmd.ExecuteScalarAsync())!;
        }

        return snapshot;
    }

    private static async Task<List<(string Table, string Pk, bool IsPerson)>> PolicyTablesAsync(SqlConnection c)
    {
        await using var cmd = new SqlCommand("""
            SELECT DISTINCT QUOTENAME (SCHEMA_NAME (o.schema_id)) + N'.' + QUOTENAME (o.name), col.name,
                   CAST (CASE WHEN sp.predicate_definition LIKE N'%tvfPersonPredicate%' THEN 1 ELSE 0 END AS BIT)
              FROM sys.security_predicates AS sp
              JOIN sys.objects AS o ON o.object_id = sp.target_object_id
              JOIN sys.indexes AS i ON i.object_id = o.object_id AND i.is_primary_key = 1
              JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
              JOIN sys.columns AS col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
             WHERE sp.predicate_type_desc = N'FILTER'
               AND (sp.predicate_definition LIKE N'%tvfTenantPredicate%' OR sp.predicate_definition LIKE N'%tvfPersonPredicate%');
            """, c);
        var list = new List<(string, string, bool)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        }

        return list;
    }
}
