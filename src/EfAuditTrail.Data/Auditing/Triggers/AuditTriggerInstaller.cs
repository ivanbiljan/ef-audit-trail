using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfAuditTrail.Data.Auditing.Triggers;

/// <summary>One audited column as the trigger sees it.</summary>
/// <param name="Column">Store column name.</param>
/// <param name="Property">CLR property name — the <c>changes</c> key, same as the interceptor's.</param>
/// <param name="Kind"><c>value</c>, <c>json</c> (leaf diff), <c>timestamptz</c> or <c>timestamp</c> (formatted like System.Text.Json).</param>
/// <param name="Pii">Null when not personal data; empty for the whole value; else JSON-pointer patterns.</param>
public sealed record AuditColumn(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("pii")] IReadOnlyList<string>? Pii);

/// <summary>The arguments of one <c>audit.enable(...)</c> call.</summary>
public sealed record AuditTableRegistration(
    string Relation,
    string EntityType,
    IReadOnlyList<string> KeyColumns,
    string? CompanyColumn,
    IReadOnlyList<AuditColumn> Columns)
{
    public string ColumnsJson => JsonSerializer.Serialize(Columns);
}

/// <summary>
/// Installs the trigger machinery (<c>audit.sql</c>) and registers every audited entity's table with it.
/// The registrations are derived from the finalized EF model — the runtime annotations
/// <see cref="AuditConvention"/> wrote from <see cref="AuditIgnoreAttribute"/>, <see cref="AuditPiiAttribute"/>
/// and <c>jsonb</c> column types — so the trigger and the interceptor can never disagree about what is audited.
/// Run it after the schema exists: from the migrator, or from a migration via <see cref="InstallScript"/>.
/// </summary>
public static class AuditTriggerInstaller
{
    public const string Timestamptz = "timestamp with time zone";
    public const string Timestamp = "timestamp without time zone";

    private static readonly Lazy<string> Script = new(() =>
    {
        using var stream = typeof(AuditTriggerInstaller).Assembly.GetManifestResourceStream("EfAuditTrail.Data.Auditing.Triggers.audit.sql")
            ?? throw new InvalidOperationException("Embedded resource audit.sql is missing.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    });

    /// <summary>The idempotent install script, for <c>migrationBuilder.Sql(...)</c>.</summary>
    public static string InstallScript => Script.Value;

    public static async Task InstallAsync(EfAuditTrailDbContext db, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Raw ADO.NET: the script is full of '{}' literals that ExecuteSqlRaw would treat as format holes.
            await ExecuteAsync(connection, transaction, InstallScript, [], cancellationToken);

            foreach (var registration in BuildRegistrations(db.Model))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "select audit.enable(@relation::text::regclass, @entity_type::text, @key_columns::text[], @company_column::text, @columns::text::jsonb)",
                    [
                        ("relation", registration.Relation),
                        ("entity_type", registration.EntityType),
                        ("key_columns", registration.KeyColumns.ToArray()),
                        ("company_column", registration.CompanyColumn),
                        ("columns", registration.ColumnsJson),
                    ],
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>Pure: what <see cref="InstallAsync"/> would register for <paramref name="model"/>.</summary>
    public static IReadOnlyList<AuditTableRegistration> BuildRegistrations(IModel model)
    {
        var registrations = new List<AuditTableRegistration>();

        foreach (var entityType in model.GetEntityTypes().OrderBy(x => x.ClrType.Name, StringComparer.Ordinal))
        {
            if (!AuditAnnotations.IsAudited(entityType) || entityType.IsOwned() || entityType.GetTableName() is not { } table)
            {
                continue;
            }

            // The store object keeps EF's own (possibly null) schema; only the regclass text defaults to public.
            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            var schema = entityType.GetSchema() ?? model.GetDefaultSchema() ?? "public";

            var key = entityType.FindPrimaryKey()
                ?? throw new InvalidOperationException($"{entityType.ClrType.Name} has no primary key, so the trigger cannot address it.");

            var companyColumn = typeof(Entities.ICompanyScoped).IsAssignableFrom(entityType.ClrType)
                ? entityType.FindProperty(nameof(Entities.ICompanyScoped.CompanyId))?.GetColumnName(store)
                : null;

            var columns = entityType.GetProperties()
                .Where(p => !AuditAnnotations.IsIgnored(p))
                .Select(p => new AuditColumn(
                    p.GetColumnName(store) ?? throw new InvalidOperationException($"{entityType.ClrType.Name}.{p.Name} is not mapped to {table}."),
                    p.Name,
                    KindOf(p),
                    p.FindRuntimeAnnotation(AuditAnnotations.PersonalData)?.Value as string[]))
                .ToList();

            registrations.Add(new AuditTableRegistration(
                $"\"{schema}\".\"{table}\"",
                entityType.ClrType.Name,
                key.Properties.Select(p => p.GetColumnName(store)!).ToList(),
                companyColumn,
                columns));
        }

        return registrations;
    }

    private static string KindOf(IProperty property)
    {
        if (AuditAnnotations.IsJson(property))
        {
            return "json";
        }

        return property.GetColumnType() switch
        {
            Timestamptz => "timestamptz",
            Timestamp => "timestamp",
            _ => "value",
        };
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
