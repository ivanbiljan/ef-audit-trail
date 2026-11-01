using EfAuditTrail.Data.Entities;

namespace EfAuditTrail.Data.Auditing;

/// <summary>
/// What the audit interceptor stamps onto every row it writes — one instance per request, circuit or job.
/// Hosts fill in actor, channel, IP and UA when the scope opens; the Warp pipeline pushes the operation.
/// </summary>
public sealed class AuditContext
{
    // AsyncLocal: a BackOffice circuit shares this instance across interleaving handlers; nested sends still share one flow.
    private readonly AsyncLocal<string?> _operation = new();

    public string? Operation
    {
        get => _operation.Value;
        private set => _operation.Value = value;
    }

    public AuditActorKind ActorKind { get; set; } = AuditActorKind.Anonymous;

    public int? ActorId { get; set; }

    public string? ActorDisplay { get; set; }

    public int? OnBehalfOfCustomerId { get; set; }

    public AuditChannel? Channel { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Pushes an operation for the life of the returned scope; disposing restores the previous one,
    /// so a retry that re-enters the pipeline on the same scope starts from where it began.</summary>
    public IDisposable BeginOperation(string code)
    {
        var previous = Operation;
        Operation = code;

        return new OperationScope(this, previous);
    }

    private sealed class OperationScope(AuditContext context, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            context.Operation = previous;
        }
    }
}
