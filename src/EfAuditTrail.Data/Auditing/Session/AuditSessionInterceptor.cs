using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfAuditTrail.Data.Auditing.Session;

/// <summary>How a command gets the scope's context to the database before the trigger needs it.</summary>
internal enum SessionDelivery
{
    /// <summary>The command cannot fire the trigger.</summary>
    None,

    /// <summary>A call inside each <c>UPDATE</c>/<c>DELETE</c>: no extra statement, no extra round trip.</summary>
    InStatements,

    /// <summary>A leading statement in the same command: no extra round trip.</summary>
    LeadingStatement,

    /// <summary>A separate command, one extra round trip.</summary>
    SeparateCommand,
}

/// <summary>
/// The bridge between <see cref="AuditContext"/> and the database trigger, which records every update and
/// delete. It publishes the scope's who/what (actor, channel, IP, UA, operation) as <c>audit.*</c> session
/// settings before anything that can write audited rows:
/// <list type="bullet">
/// <item><c>SaveChanges</c> — <c>audit.publish(…)</c> inside each statement's <c>WHERE</c>. EF reads rows
/// affected by statement index, so a leading statement would hide stale rows from its concurrency check. The
/// call has to be on every statement: a stale first row would otherwise leave the next one without context.</item>
/// <item><c>ExecuteUpdate</c>, <c>ExecuteDelete</c> — a leading statement in the same command.</item>
/// <item>anything else (<c>ExecuteSql</c>) — a separate command, because raw SQL may not be allowed in a
/// multi-statement command at all (<c>VACUUM</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// Settings are session-level (<c>set_config(..., false)</c>) rather than transaction-local, because
/// <c>ExecuteUpdate</c> outside a transaction runs in autocommit and a local setting would be gone by the
/// time the UPDATE runs. They are re-sent with every write, so a pooled connection, a rolled back
/// transaction (which reverts settings) or a different scope can never leave stale context behind.
/// </remarks>
public sealed class AuditSessionInterceptor(AuditContext context) : DbCommandInterceptor
{
    // Prefixed: these parameters share a command with EF's, which are named after captured variables.
    private const string ParameterPrefix = "__audit_";

    internal const string SetSessionSql =
        "select set_config('audit.operation', @__audit_operation, false), "
        + "set_config('audit.actor_kind', @__audit_actor_kind, false), "
        + "set_config('audit.actor_id', @__audit_actor_id, false), "
        + "set_config('audit.actor_display', @__audit_actor_display, false), "
        + "set_config('audit.on_behalf_of_customer_id', @__audit_on_behalf_of_customer_id, false), "
        + "set_config('audit.channel', @__audit_channel, false), "
        + "set_config('audit.ip_address', @__audit_ip_address, false), "
        + "set_config('audit.user_agent', @__audit_user_agent, false)";

    private const string PublishClause =
        "WHERE audit.publish(@__audit_operation, @__audit_actor_kind, @__audit_actor_id, @__audit_actor_display, "
        + "@__audit_on_behalf_of_customer_id, @__audit_channel, @__audit_ip_address, @__audit_user_agent) AND ";

    private static readonly Regex WriteStatement = new("^(UPDATE|DELETE) ", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex WhereClause = new("^WHERE ", RegexOptions.Multiline | RegexOptions.Compiled);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        using var session = PrepareSession(command, eventData);
        session?.ExecuteNonQuery();

        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await using var session = PrepareSession(command, eventData);
        if (session is not null)
        {
            await session.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        using var session = PrepareSession(command, eventData);
        session?.ExecuteNonQuery();

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await using var session = PrepareSession(command, eventData);
        if (session is not null)
        {
            await session.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        using var session = PrepareSession(command, eventData);
        session?.ExecuteNonQuery();

        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await using var session = PrepareSession(command, eventData);
        if (session is not null)
        {
            await session.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }

    internal static SessionDelivery Classify(CommandSource source) => source switch
    {
        // Plain reads cannot fire an UPDATE/DELETE trigger; schema creation has nothing to audit.
        CommandSource.LinqQuery or CommandSource.Migrations or CommandSource.ValueGenerator => SessionDelivery.None,
        CommandSource.SaveChanges => SessionDelivery.InStatements,
        CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete => SessionDelivery.LeadingStatement,
        _ => SessionDelivery.SeparateCommand,
    };

    /// <summary>
    /// Adds <c>audit.publish(…)</c> to every <c>UPDATE</c>/<c>DELETE</c> of a <c>SaveChanges</c> command. Text
    /// is safe to match here because EF sends values as parameters, so the only SQL in it is its own. Throws if
    /// a statement does not have the expected shape: running it would record the change with no context.
    /// </summary>
    internal static string AddPublishClause(string sql)
    {
        var writes = WriteStatement.Count(sql);
        if (writes == 0)
        {
            return sql;
        }

        if (WhereClause.Count(sql) != writes)
        {
            throw new InvalidOperationException(
                $"Could not add audit.publish to {writes} UPDATE/DELETE statement(s), so they would be audited without context: {sql}");
        }

        return WhereClause.Replace(sql, PublishClause);
    }

    /// <summary>Puts the context on the command itself where it can; otherwise returns the command to run first.</summary>
    private DbCommand? PrepareSession(DbCommand command, CommandEventData eventData)
    {
        switch (Classify(eventData.CommandSource))
        {
            case SessionDelivery.InStatements:
                var sql = AddPublishClause(command.CommandText);
                if (!ReferenceEquals(sql, command.CommandText))
                {
                    RequireChannel();
                    command.CommandText = sql;
                    AddSessionParameters(command);
                }

                return null;

            case SessionDelivery.LeadingStatement:
                RequireChannel();
                command.CommandText = SetSessionSql + ";" + Environment.NewLine + command.CommandText;
                AddSessionParameters(command);

                return null;

            case SessionDelivery.SeparateCommand when command.Connection is not null:
                var session = command.Connection.CreateCommand();
                session.Transaction = command.Transaction;
                session.CommandText = SetSessionSql;
                AddSessionParameters(session);

                return session;

            default:
                return null;
        }
    }

    // Before the statement runs, so a host that never wired the channel fails the write instead of having it
    // recorded as an out-of-band change by the database role.
    private void RequireChannel()
    {
        if (context.Channel is null)
        {
            throw new InvalidOperationException("AuditContext.Channel is not set — the host did not wire the audit channel");
        }
    }

    private void AddSessionParameters(DbCommand target)
    {
        Add(target, "operation", context.Operation);
        Add(target, "actor_kind", context.ActorKind.ToString());
        Add(target, "actor_id", context.ActorId?.ToString(CultureInfo.InvariantCulture));
        Add(target, "actor_display", context.ActorDisplay);
        Add(target, "on_behalf_of_customer_id", context.OnBehalfOfCustomerId?.ToString(CultureInfo.InvariantCulture));
        Add(target, "channel", context.Channel?.ToString());
        Add(target, "ip_address", context.IpAddress);
        Add(target, "user_agent", context.UserAgent);
    }

    // '' rather than NULL: set_config(…, NULL, …) is an error, and audit.ctx() reads '' back as NULL.
    private static void Add(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = ParameterPrefix + name;
        parameter.Value = value ?? string.Empty;
        command.Parameters.Add(parameter);
    }
}
