using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfAuditTrail.Data.Auditing;

/// <summary>Runtime annotations written by <see cref="AuditConvention"/> at model build and read by the
/// interceptor at save time, so no reflection happens per save. Runtime annotations are never serialized
/// into the migrations snapshot.</summary>
public static class AuditAnnotations
{
    public const string Enabled = "audit:enabled";
    public const string Ignored = "audit:ignored";
    public const string PersonalData = "audit:pii";
    public const string Json = "audit:json";

    public static bool IsAudited(IEntityType entityType) => IsSet(entityType, Enabled);

    public static bool IsIgnored(IProperty property) => IsSet(property, Ignored);

    /// <summary>Whether the <c>changes</c> key at <paramref name="path"/> — a JSON pointer below the property,
    /// empty for the property itself — holds personal data. A <paramref name="container"/> value is a subtree
    /// snapshot (a delete, a type change), so it counts whenever the property has personal paths at all.</summary>
    public static bool IsPersonalData(IProperty property, string path, bool container)
    {
        if (property.FindRuntimeAnnotation(PersonalData)?.Value is not string[] paths)
        {
            return false;
        }

        return paths.Length == 0 || container || paths.Any(pattern => Matches(pattern.Split('/'), path.Split('/')));
    }

    public static bool IsJson(IProperty property) => IsSet(property, Json);

    // True when the path is at or below the pattern: `*` matches one segment, `**` any number.
    private static bool Matches(ReadOnlySpan<string> pattern, ReadOnlySpan<string> path)
    {
        if (pattern.IsEmpty)
        {
            return true;
        }

        if (pattern[0] == "**")
        {
            return Matches(pattern[1..], path) || (!path.IsEmpty && Matches(pattern, path[1..]));
        }

        return !path.IsEmpty && (pattern[0] == "*" || pattern[0] == path[0]) && Matches(pattern[1..], path[1..]);
    }

    private static bool IsSet(IAnnotatable annotatable, string name)
        => annotatable.FindRuntimeAnnotation(name)?.Value is true;
}
