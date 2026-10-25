using System.Data;
using Dapper;
using Merit.Application.Errors;
using Merit.Application.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Merit.Data.SqlServer;

public sealed class MeritDatabaseOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public int CommandTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Opens connections and calls stored procedures. Every connection gets the current session's scope key, user id, user
/// name and acting tenant through sp_set_session_context with @read_only = 1, so nothing later on the connection can
/// change them; row-level security predicates read them. Pooled connections are reset (sp_reset_connection) before
/// reuse, which clears session context, so each open sets it afresh. With no session the predicates fail closed.
/// </summary>
public sealed class Db(IOptions<MeritDatabaseOptions> options, ICurrentSession session)
{
    private const string SetContextSql =
        "EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @scopeKey, @read_only = 1;" +
        "EXEC sys.sp_set_session_context @key = N'appUserId', @value = @appUserId, @read_only = 1;" +
        "EXEC sys.sp_set_session_context @key = N'AppUser', @value = @appUser, @read_only = 1;" +
        "EXEC sys.sp_set_session_context @key = N'actingTenantId', @value = @actingTenantId, @read_only = 1;";

    static Db()
    {
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
    }

    private int Timeout => options.Value.CommandTimeoutSeconds;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        if (session.ScopeKey is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(SetContextSql, new
            {
                scopeKey = session.ScopeKey,
                appUserId = session.UserId,
                appUser = session.AuditName,
                actingTenantId = session.ActingTenantId,
            }, commandTimeout: Timeout, cancellationToken: ct)).ConfigureAwait(false);
        }

        return connection;
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string procedure, object? parameters, CancellationToken ct)
        => await Run(async c => (IReadOnlyList<T>)(await c.QueryAsync<T>(Command(procedure, parameters, ct)).ConfigureAwait(false)).AsList(), ct)
            .ConfigureAwait(false);

    public async Task<T?> QuerySingleAsync<T>(string procedure, object? parameters, CancellationToken ct)
        => await Run(async c => await c.QueryFirstOrDefaultAsync<T>(Command(procedure, parameters, ct)).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

    public async Task ExecuteAsync(string procedure, object? parameters, CancellationToken ct)
        => await Run(async c => await c.ExecuteAsync(Command(procedure, parameters, ct)).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<T> MultipleAsync<T>(string procedure, object? parameters, Func<SqlMapper.GridReader, Task<T>> read, CancellationToken ct)
        => await Run(async c =>
        {
            using var grid = await c.QueryMultipleAsync(Command(procedure, parameters, ct)).ConfigureAwait(false);
            return await read(grid).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    private CommandDefinition Command(string procedure, object? parameters, CancellationToken ct)
        => new(procedure, parameters, commandType: CommandType.StoredProcedure, commandTimeout: Timeout, cancellationToken: ct);

    private async Task<T> Run<T>(Func<SqlConnection, Task<T>> work, CancellationToken ct)
    {
        try
        {
            var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await work(connection).ConfigureAwait(false);
            }
        }
        catch (SqlException ex)
        {
            throw Map(ex);
        }
    }

    /// <summary>
    /// Maps the error numbers the procedures raise (see tools/generateProcedures.py) to application exceptions. Other
    /// errors pass through unchanged so deadlocks (1205) and constraint violations (2627, 547) keep their numbers.
    /// </summary>
    private static Exception Map(SqlException ex) => ex.Number switch
    {
        51403 => new PermissionDeniedException(ex.Message, ex),
        51404 => new NotFoundException(ex.Message, ex),
        51409 => new ConcurrencyException(ex.Message, ex),
        51422 => new BusinessRuleException(ex.Message, ex),
        50000 => new BusinessRuleException(ex.Message, ex),
        2601 or 2627 => new BusinessRuleException("That would duplicate a record that already exists.", ex),
        547 => new BusinessRuleException("That change conflicts with a related record.", ex),
        _ => ex,
    };

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => value switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            DateOnly d => d,
            _ => DateOnly.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override DateOnly? Parse(object value) => value is null or DBNull ? null : new DateOnlyHandler().Parse(value);

        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value is null ? DBNull.Value : value.Value.ToDateTime(TimeOnly.MinValue);
        }
    }
}
