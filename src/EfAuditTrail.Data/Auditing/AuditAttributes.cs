namespace EfAuditTrail.Data.Auditing;

/// <summary>Opts a class or property out of the audit trail. Auditing is opt-out — everything mapped is
/// audited unless marked.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute;

/// <summary>Marks a property carrying personal data, so an audit row lists its <c>changes</c> keys in
/// <c>pii_paths</c>. On a jsonb property, <c>paths</c> narrows it to the JSON
/// pointers holding personal data (<c>*</c> matches one segment, <c>**</c> any number); none marks the whole value.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditPiiAttribute(params string[] paths) : Attribute
{
    public string[] Paths { get; } = paths;
}
