using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>Where the integration database lives and how to reach it. See the project file for the settings.</summary>
public static partial class TestEnvironment
{
    public static string Server { get; } = Environment.GetEnvironmentVariable("MERIT_IT_SERVER") ?? @"localhost\PORTAL";

    public static string Database { get; } = Environment.GetEnvironmentVariable("MERIT_IT_DATABASE") ?? "MERIT_IT";

    public static string SourceRoot { get; } = FindSourceRoot();

    /// <summary>Deployment identity (Windows authentication, db_owner): deploys, resets MFA for test users, takes snapshots.</summary>
    public static string AdminConnectionString(string? database = null)
        => new SqlConnectionStringBuilder
        {
            DataSource = Server, InitialCatalog = database ?? Database, IntegratedSecurity = true, TrustServerCertificate = true,
            Encrypt = true, ConnectTimeout = 5, ApplicationName = "Merit.IntegrationTests (admin)",
        }.ConnectionString;

    /// <summary>The web application's login: EXECUTE on procedures only, filtered by row-level security.</summary>
    public static string WebConnectionString { get; } = new SqlConnectionStringBuilder
    {
        DataSource = Server, InitialCatalog = Database, UserID = "meritWeb", Password = WebPassword(), TrustServerCertificate = true,
        Encrypt = true, ApplicationName = "Merit.IntegrationTests (web)",
    }.ConnectionString;

    private static readonly Lazy<string?> Unavailable = new(Probe);

    /// <summary>Null when the server answers; otherwise why the suite is skipped.</summary>
    public static string? SkipReason => Unavailable.Value;

    private static string? Probe()
    {
        try
        {
            using var c = new SqlConnection(AdminConnectionString("master"));
            c.Open();
            return null;
        }
        catch (SqlException ex)
        {
            return $"SQL Server {Server} is not reachable ({ex.Message}). Set MERIT_IT_SERVER to run the integration suite.";
        }
    }

    private static string WebPassword()
    {
        if (Environment.GetEnvironmentVariable("MERIT_IT_WEB_PASSWORD") is { Length: > 0 } fromEnv)
        {
            return fromEnv;
        }

        var settings = Path.Combine(SourceRoot, "src", "Merit.Web", "appsettings.Development.json");
        if (File.Exists(settings))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) && cs.TryGetProperty("Merit", out var merit)
                && PasswordPattern().Match(merit.GetString() ?? string.Empty) is { Success: true } m)
            {
                return m.Groups[1].Value;
            }
        }

        return string.Empty;
    }

    /// <summary>Runs Deploy-MeritDatabase.ps1 for the test database, with the demo data. Re-runnable, so safe every run.</summary>
    public static void Deploy()
    {
        if (Environment.GetEnvironmentVariable("MERIT_IT_SKIP_DEPLOY") == "1")
        {
            return;
        }

        var script = Path.Combine(SourceRoot, "database", "Deploy-MeritDatabase.ps1");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ServerInstance \"{Server}\" -DatabaseName {Database} -IncludeDemoData")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            p.Kill(true);
            throw new InvalidOperationException("The test database deployment timed out.");
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Deploying {Database} failed:\n{stdout.Result[^Math.Min(4000, stdout.Result.Length)..]}\n{stderr.Result}");
        }
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Merit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find Merit.sln above the test output folder.");
    }

    [GeneratedRegex("Password=([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPattern();
}

/// <summary>A fact that is skipped, with the reason, when the integration SQL Server cannot be reached.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (TestEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute()
    {
        if (TestEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
