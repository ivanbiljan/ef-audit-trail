namespace EfAuditTrail.Data.Entities;

public enum AuditAction
{
    Update,
    Delete,
}

public enum AuditActorKind
{
    Anonymous,
    Customer,
    Staff,
    System,
}

public enum AuditChannel
{
    Api,
    BackOffice,
    Job,
    Seeder,

    /// <summary>A write that reached the database without the application's session context — psql, a
    /// migration script, another service. Only the trigger can see these.</summary>
    Database,
}

/// <summary>Which capture path produced the row. All three write the same shape, so readers never care —
/// it is here to compare the approaches side by side.</summary>
public enum AuditSource
{
    /// <summary>The EF <c>SaveChanges</c> interceptor (<c>AuditInterceptor</c>).</summary>
    Interceptor,

    /// <summary>The <c>audit.capture()</c> row trigger — bulk statements and out-of-band SQL.</summary>
    Trigger,

    /// <summary><c>ExecuteAuditedUpdateAsync</c> / <c>ExecuteAuditedDeleteAsync</c>: lock, snapshot, run, snapshot, diff.</summary>
    Snapshot,
}

/// <summary>One audited change: who (actor, channel, IP, UA), when (<see cref="OccurredAt"/>) and what
/// (<see cref="Changes"/> — a flat map of property name or JSON pointer to <c>{ old, new }</c>).</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public int? CompanyId { get; set; }

    public required string Operation { get; set; }

    public AuditAction Action { get; set; }

    public required string EntityType { get; set; }

    public required string EntityKey { get; set; }

    public AuditActorKind ActorKind { get; set; }

    public int? ActorId { get; set; }

    public string? ActorDisplay { get; set; }

    public int? OnBehalfOfCustomerId { get; set; }

    public AuditChannel Channel { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>jsonb: <c>{ "Name": { "old": "…", "new": "…" }, "Profile/deliveryContact/phone": { … } }</c>.</summary>
    public required string Changes { get; set; }

    /// <summary>jsonb array of the <see cref="Changes"/> keys that hold personal data.</summary>
    public required string PiiPaths { get; set; }

    public AuditSource Source { get; set; } = AuditSource.Interceptor;
}
