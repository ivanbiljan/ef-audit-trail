using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfAuditTrail.Data.Auditing;

/// <summary>
/// Writes the audit rows for a save into the same <see cref="EfAuditTrailDbContext"/>, so they reach
/// the database in the transaction that carries the business write. Only updates and deletes are audited,
/// so a successful save needs no follow-up; a failed, cancelled or conflicted one has its pending rows discarded.
/// </summary>
public sealed class AuditInterceptor(AuditContext context) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CaptureAudit(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CaptureAudit(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DiscardAudit(eventData.Context);

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        DiscardAudit(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    // EF raises only the Canceled / ThrowingConcurrencyException events for these outcomes, never SaveChangesFailed.
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        DiscardAudit(eventData.Context);

        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        DiscardAudit(eventData.Context);
        base.SaveChangesCanceled(eventData);
    }

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        DiscardAudit(eventData.Context);

        return base.ThrowingConcurrencyExceptionAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        DiscardAudit(eventData.Context);

        return base.ThrowingConcurrencyException(eventData, result);
    }

    /// <summary>A Blazor circuit shares one context, so rows left pending after a rejected save would
    /// replay on the next successful one.</summary>
    private static void DiscardAudit(DbContext? dbContext)
    {
        if (dbContext is null)
        {
            return;
        }

        var pending = dbContext.ChangeTracker.Entries<AuditEntry>()
            .Where(x => x.State == EntityState.Added)
            .ToList();

        foreach (var entry in pending)
        {
            entry.State = EntityState.Detached;
        }
    }

    private void CaptureAudit(DbContext? dbContext)
    {
        if (dbContext is not EfAuditTrailDbContext db)
        {
            return;
        }

        var rows = AuditEntryBuilder.Build(db, context, DateTime.UtcNow);
        if (rows.Count > 0)
        {
            db.AuditEntries.AddRange(rows);
        }
    }
}
