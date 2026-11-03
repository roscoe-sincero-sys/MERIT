using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// Session scopes a test creates straight through auth.uspCreateSessionScope, revoked when the test ends, as sign-out
/// would (issue #187). Left open, they looked like a demo user's newest session to later tests.
/// </summary>
public static class TestSessionScopes
{
    private static readonly ConcurrentQueue<Guid> Open = new();

    public static Guid Track(Guid sessionScopeId)
    {
        Open.Enqueue(sessionScopeId);
        return sessionScopeId;
    }

    public static async Task RevokeAllAsync()
    {
        var ids = new List<Guid>();
        while (Open.TryDequeue(out var id))
        {
            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            return;
        }

        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString());
        await c.OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE revokedUtc IS NULL AND sessionScopeId IN (SELECT CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (@ids))", c);
        cmd.Parameters.AddWithValue("@ids", System.Text.Json.JsonSerializer.Serialize(ids));
        await cmd.ExecuteNonQueryAsync();
    }
}
