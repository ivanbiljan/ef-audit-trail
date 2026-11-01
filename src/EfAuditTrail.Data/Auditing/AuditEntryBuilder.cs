using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfAuditTrail.Data.Auditing;

/// <summary>Turns the ChangeTracker's Modified and Deleted entries into audit rows. Pure — the caller adds
/// them to the same context, so they commit with the business write.</summary>
public static class AuditEntryBuilder
{
    private const int ActorDisplayLength = 320;
    private const int UserAgentLength = 512;

    public static IReadOnlyList<AuditEntry> Build(EfAuditTrailDbContext db, AuditContext context, DateTime utcNow)
    {
        var rows = new List<AuditEntry>();

        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Metadata.ClrType == typeof(AuditEntry) || !AuditAnnotations.IsAudited(entry.Metadata))
            {
                continue;
            }

            var row = BuildRow(context, utcNow, entry);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static AuditEntry? BuildRow(AuditContext context, DateTime utcNow, EntityEntry entry)
    {
        var deleted = entry.State == EntityState.Deleted;
        var changes = new JsonObject();
        var piiPaths = new List<string>();

        foreach (var property in entry.Properties)
        {
            if (AuditAnnotations.IsIgnored(property.Metadata))
            {
                continue;
            }

            var keys = Emit(changes, property, deleted);
            var name = property.Metadata.Name;

            piiPaths.AddRange(keys.Where(key =>
                AuditAnnotations.IsPersonalData(property.Metadata, key[name.Length..], IsContainer(changes[key]!))));
        }

        // A deleted key-only join row has nothing but its key, and the removal still has to be recorded.
        if (!deleted && changes.Count == 0)
        {
            return null;
        }

        var entityType = entry.Metadata.ClrType.Name;
        var entityKey = KeyOf(entry);

        // Before the row exists, so a host that never wired the channel fails the whole save, not half of it.
        var channel = context.Channel ?? throw new InvalidOperationException(
            $"{entityType} {entityKey}: AuditContext.Channel is not set — the host did not wire the audit channel");

        return new AuditEntry
        {
            OccurredAt = utcNow,
            CompanyId = (entry.Entity as ICompanyScoped)?.CompanyId,
            Operation = context.Operation ?? "unattributed",
            Action = deleted ? AuditAction.Delete : AuditAction.Update,
            EntityType = entityType,
            EntityKey = entityKey,
            ActorKind = context.ActorKind,
            ActorId = context.ActorId,
            ActorDisplay = Truncate(context.ActorDisplay, ActorDisplayLength),
            OnBehalfOfCustomerId = context.OnBehalfOfCustomerId,
            Channel = channel,
            IpAddress = context.IpAddress,
            UserAgent = Truncate(context.UserAgent, UserAgentLength),
            Changes = changes.ToJsonString(),
            PiiPaths = JsonSerializer.Serialize(piiPaths),
        };
    }

    /// <summary>Returns the <c>changes</c> keys this property contributed, so the caller can mark PII ones.</summary>
    private static List<string> Emit(JsonObject changes, PropertyEntry property, bool deleted)
    {
        var name = property.Metadata.Name;

        if (!deleted && !property.IsModified)
        {
            return [];
        }

        var oldValue = ToProvider(property.Metadata, property.OriginalValue);
        var newValue = deleted ? null : ToProvider(property.Metadata, property.CurrentValue);

        if (AuditAnnotations.IsJson(property.Metadata))
        {
            // A delete snapshots the document whole; only an update is worth a leaf diff.
            if (deleted)
            {
                changes[name] = Change(ParseJson(oldValue as string), null);

                return [name];
            }

            var keys = new List<string>();

            foreach (var (path, values) in JsonDelta.Compute(oldValue as string, newValue as string))
            {
                var key = name + path;
                changes[key] = Change(values.Old, values.New);
                keys.Add(key);
            }

            return keys;
        }

        // The value compare as well as IsModified: a reference-typed provider value reads as a fresh
        // instance each time, so IsModified alone would report every such property as changed.
        if (!deleted && Equals(oldValue, newValue))
        {
            return [];
        }

        changes[name] = Change(ToNode(oldValue), ToNode(newValue));

        return [name];
    }

    private static object? ToProvider(IProperty property, object? value)
    {
        // HasConversion<string>() leaves GetValueConverter() null and resolves the converter on the type
        // mapping instead, which is where every enum-as-string property carries it.
        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;

        return converter is null ? value : converter.ConvertToProvider(value);
    }

    private static bool IsContainer(JsonNode change) =>
        change["old"] is JsonObject or JsonArray || change["new"] is JsonObject or JsonArray;

    private static JsonNode? ToNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value);

    private static JsonNode? ParseJson(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);

    private static JsonObject Change(JsonNode? oldValue, JsonNode? newValue) =>
        new() { ["old"] = oldValue, ["new"] = newValue };

    private static string KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()
            ?? throw new InvalidOperationException(
                $"{entry.Metadata.ClrType.Name} has no primary key, so an audit row cannot address it.");

        // Original, not current: a Deleted entry still has it.
        return string.Join("|", key.Properties.Select(p => Format(entry.Property(p.Name).OriginalValue)));
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;
}
