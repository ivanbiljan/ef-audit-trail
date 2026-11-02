using System.Reflection;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace EfAuditTrail.Data.Auditing;

/// <summary>
/// Resolves the audit attributes once, at model build, into runtime annotations the trigger installer
/// reads. Runtime annotations only — a model annotation would be serialized into the migrations snapshot.
/// </summary>
internal sealed class AuditConvention : IModelFinalizedConvention
{
    public IModel ProcessModelFinalized(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var enabled = IsAudited(entityType);
            entityType.SetRuntimeAnnotation(AuditAnnotations.Enabled, enabled);

            if (!enabled)
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                Annotate(entityType, property);
            }
        }

        return model;
    }

    private static bool IsAudited(IEntityType entityType)
    {
        // Warp's message store arrives through its own IModelCustomizer and is infrastructure, not business state.
        if (entityType.GetSchema() == "warp")
        {
            return false;
        }

        if (entityType.ClrType == typeof(AuditEntry))
        {
            return false;
        }

        return !entityType.ClrType.IsDefined(typeof(AuditIgnoreAttribute), inherit: false);
    }

    private static void Annotate(IEntityType entityType, IProperty property)
    {
        if (IsIgnored(entityType, property))
        {
            property.SetRuntimeAnnotation(AuditAnnotations.Ignored, true);
        }

        // Read the configured column type off the annotation rather than via GetColumnType(): that resolves a
        // relational type mapping, which the InMemory provider has none of (tests build the model on it).
        var columnType = property.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value as string;
        var json = string.Equals(columnType, "jsonb", StringComparison.OrdinalIgnoreCase);

        if (json)
        {
            property.SetRuntimeAnnotation(AuditAnnotations.Json, true);
        }

        if (property.PropertyInfo?.GetCustomAttribute<AuditPiiAttribute>() is not { } pii)
        {
            return;
        }

        if (pii.Paths.Length > 0 && !json)
        {
            throw new InvalidOperationException(
                $"{entityType.ClrType.Name}.{property.Name}: [AuditPii] paths apply only to a jsonb property.");
        }

        property.SetRuntimeAnnotation(AuditAnnotations.PersonalData, pii.Paths);
    }

    private static bool IsIgnored(IEntityType entityType, IProperty property)
    {
        if (property.IsPrimaryKey())
        {
            return true;
        }

        var clrType = entityType.ClrType;

        if (typeof(IHaveTimestamps).IsAssignableFrom(clrType)
            && property.Name is nameof(IHaveTimestamps.CreatedAt) or nameof(IHaveTimestamps.UpdatedAt))
        {
            return true;
        }

        if (typeof(ICompanyScoped).IsAssignableFrom(clrType) && property.Name == nameof(ICompanyScoped.CompanyId))
        {
            return true;
        }

        return property.PropertyInfo?.IsDefined(typeof(AuditIgnoreAttribute)) == true;
    }
}
