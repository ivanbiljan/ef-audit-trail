using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfAuditTrail.Data.Auditing.Session;

/// <summary>
/// The bridge between <see cref="AuditContext"/> and the database trigger. Before every command that can
/// write audited rows it publishes the scope's who/what (actor, channel, IP, UA, operation) as <c>audit.*</c>
/// session settings, and tells the trigger whether the application is auditing that statement itself:
/// <list type="bullet">
/// <item><c>SaveChanges</c> — the <see cref="AuditInterceptor"/> writes the rows, so the trigger stands down;</item>
/// <item>inside <see cref="TriggerCapture.Suppress"/> — the snapshot bulk path writes the rows;</item>
/// <item>anything else (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, <c>ExecuteSql</c>) — the trigger records it.</item>
/// </list>
/// It also appends <c>FOR UPDATE</c> to queries tagged <see cref="AuditTags.ForUpdate"/>.
/// </summary>
/// <remarks>
/// Settings are session-level (<c>set_config(..., false)</c>) rather than transaction-local, because
/// <c>ExecuteUpdate</c> outside a transaction runs in autocommit and a local setting would be gone by the
/// time the UPDATE runs. They are re-sent before every writing command, so a pooled connection, a rolled
/// back transaction (which reverts settings) or a different scope can never leave stale context behind.
/// That costs one extra round trip per write; reads (LINQ queries) are skipped.
/// </remarks>
public sealed class AuditSessionInterceptor(AuditContext context) : DbCommandInterceptor
{
    internal const string SetSessionSql =
        "select set_config('audit.suppress', @suppress, false), "
        + "set_config('audit.operation', @operation, false), "
        + "set_config('audit.actor_kind', @actor_kind, false), "
        + "set_config('audit.actor_id', @actor_id, false), "
        + "set_config('audit.actor_display', @actor_display, false), "
        + "set_config('audit.on_behalf_of_customer_id', @on_behalf_of_customer_id, false), "
        + "set_config('audit.channel', @channel, false), "
        + "set_config('audit.ip_address', @ip_address, false), "
        + "set_config('audit.user_agent', @user_agent, false)";

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ApplyForUpdate(command);
        using var session = CreateSessionCommand(command, eventData);
        session?.ExecuteNonQuery();

        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ApplyForUpdate(command);
        await using var session = CreateSessionCommand(command, eventData);
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
        using var session = CreateSessionCommand(command, eventData);
        session?.ExecuteNonQuery();

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await using var session = CreateSessionCommand(command, eventData);
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
        using var session = CreateSessionCommand(command, eventData);
        session?.ExecuteNonQuery();

        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await using var session = CreateSessionCommand(command, eventData);
        if (session is not null)
        {
            await session.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>Whether a command needs the session published, and whether the trigger should stand down for it.</summary>
    internal static (bool Publish, bool Suppress) Classify(CommandSource source, bool suppressScope) => source switch
    {
        // Plain reads cannot fire an UPDATE/DELETE trigger; schema creation has nothing to audit.
        CommandSource.LinqQuery or CommandSource.Migrations or CommandSource.ValueGenerator => (false, false),
        CommandSource.SaveChanges => (true, true),
        _ => (true, suppressScope),
    };

    private static void ApplyForUpdate(DbCommand command)
    {
        // EF writes tags as leading "-- tag" comment lines. Row locks go last: SELECT … ORDER BY … LIMIT … FOR UPDATE.
        if (command.CommandText.Contains("-- " + AuditTags.ForUpdate, StringComparison.Ordinal))
        {
            command.CommandText = command.CommandText.TrimEnd().TrimEnd(';') + Environment.NewLine + "FOR UPDATE";
        }
    }

    private DbCommand? CreateSessionCommand(DbCommand command, CommandEventData eventData)
    {
        var (publish, suppress) = Classify(eventData.CommandSource, TriggerCapture.IsSuppressed);
        if (!publish || command.Connection is null)
        {
            return null;
        }

        var session = command.Connection.CreateCommand();
        session.Transaction = command.Transaction;
        session.CommandText = SetSessionSql;

        Add(session, "suppress", suppress ? "on" : null);
        Add(session, "operation", context.Operation);
        Add(session, "actor_kind", context.ActorKind.ToString());
        Add(session, "actor_id", context.ActorId?.ToString(CultureInfo.InvariantCulture));
        Add(session, "actor_display", context.ActorDisplay);
        Add(session, "on_behalf_of_customer_id", context.OnBehalfOfCustomerId?.ToString(CultureInfo.InvariantCulture));
        Add(session, "channel", context.Channel?.ToString());
        Add(session, "ip_address", context.IpAddress);
        Add(session, "user_agent", context.UserAgent);

        return session;
    }

    // '' rather than NULL: set_config(…, NULL, …) is an error, and audit.ctx() reads '' back as NULL.
    private static void Add(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? string.Empty;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Tags the snapshot path puts on its queries.</summary>
public static class AuditTags
{
    /// <summary>Makes <see cref="AuditSessionInterceptor"/> append <c>FOR UPDATE</c> to the tagged query.</summary>
    public const string ForUpdate = "audit:for-update";
}

/// <summary>
/// Marks the current async flow as "the application audits these statements itself", so the trigger stands
/// down. An <see cref="AsyncLocal{T}"/> rather than a query tag: it does not depend on the provider carrying
/// tags into <c>ExecuteUpdate</c> SQL, and it cannot leak to another request.
/// </summary>
public static class TriggerCapture
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsSuppressed => Depth.Value > 0;

    public static IDisposable Suppress()
    {
        Depth.Value++;

        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Depth.Value--;
        }
    }
}
