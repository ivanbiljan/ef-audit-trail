using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfAuditTrail.Data.Auditing;

/// <summary>Runtime annotations written by <see cref="AuditConvention"/> at model build and read by
/// <see cref="Triggers.AuditTriggerInstaller"/>, so the model is the only place audit rules live. Runtime annotations are never serialized
/// into the migrations snapshot.</summary>
public static class AuditAnnotations
{
    public const string Enabled = "audit:enabled";
    public const string Ignored = "audit:ignored";
    public const string PersonalData = "audit:pii";
    public const string Json = "audit:json";

    public static bool IsAudited(IEntityType entityType) => IsSet(entityType, Enabled);

    public static bool IsIgnored(IProperty property) => IsSet(property, Ignored);

    public static bool IsJson(IProperty property) => IsSet(property, Json);

    private static bool IsSet(IAnnotatable annotatable, string name)
        => annotatable.FindRuntimeAnnotation(name)?.Value is true;
}
