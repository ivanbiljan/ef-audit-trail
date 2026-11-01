using EfAuditTrail.Data.Auditing.Session;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfAuditTrail.Data.Auditing.Snapshots;

/// <summary>
/// <c>ExecuteUpdateAsync</c> / <c>ExecuteDeleteAsync</c> that leave the same audit rows <c>SaveChanges</c> would.
/// <para>
/// In one transaction (the caller's, or one opened here): read the matching rows <c>FOR UPDATE</c> (the
/// "before" snapshot — locked, so nothing can change them until commit), run the bulk statement restricted
/// to exactly those keys, read them again (the "after" snapshot), then replay each before/after pair through
/// a scratch change tracker and hand it to the existing <see cref="AuditEntryBuilder"/>. Reusing the builder
/// means value conversion, JSON leaf diffs, PII paths and ignored properties match the interceptor exactly.
/// </para>
/// <para>
/// The trigger is told to stand down for the bulk statement (<see cref="TriggerCapture"/>), so the change is
/// recorded once. Rows a concurrent insert adds after the snapshot are not touched — the statement is pinned
/// to the locked keys, which is what makes the audit exact.
/// </para>
/// </summary>
public static class AuditedBulkExtensions
{
    public static Task<int> ExecuteAuditedUpdateAsync<T>(
        this IQueryable<T> source,
        EfAuditTrailDbContext db,
        Action<UpdateSettersBuilder<T>> setters,
        CancellationToken cancellationToken = default)
        where T : class
        => RunAsync(db, source, (target, ct) => target.ExecuteUpdateAsync(setters, ct), delete: false, cancellationToken);

    public static Task<int> ExecuteAuditedDeleteAsync<T>(
        this IQueryable<T> source,
        EfAuditTrailDbContext db,
        CancellationToken cancellationToken = default)
        where T : class
        => RunAsync(db, source, (target, ct) => target.ExecuteDeleteAsync(ct), delete: true, cancellationToken);

    private static async Task<int> RunAsync<T>(
        EfAuditTrailDbContext db,
        IQueryable<T> source,
        Func<IQueryable<T>, CancellationToken, Task<int>> bulk,
        bool delete,
        CancellationToken cancellationToken)
        where T : class
    {
        var entityType = db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not part of the model.");

        if (!AuditAnnotations.IsAudited(entityType))
        {
            return await bulk(source, cancellationToken);
        }

        if (db.Database.CurrentTransaction is not null)
        {
            return await CaptureAsync(db, entityType, source, bulk, delete, cancellationToken);
        }

        // Our own transaction, wrapped in the execution strategy so a retrying provider replays the whole unit.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var affected = await CaptureAsync(db, entityType, source, bulk, delete, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return affected;
        });
    }

    private static async Task<int> CaptureAsync<T>(
        EfAuditTrailDbContext db,
        IEntityType entityType,
        IQueryable<T> source,
        Func<IQueryable<T>, CancellationToken, Task<int>> bulk,
        bool delete,
        CancellationToken cancellationToken)
        where T : class
    {
        var key = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no primary key, so an audit row cannot address it.");

        var before = await source.AsNoTracking().TagWith(AuditTags.ForUpdate).ToListAsync(cancellationToken);
        if (before.Count == 0)
        {
            return 0;
        }

        // Exactly the locked rows. IgnoreQueryFilters: the source already applied them when it chose these keys.
        var filter = KeyFilter.For(key, before);
        var target = db.Set<T>().IgnoreQueryFilters().Where(filter);

        int affected;
        using (TriggerCapture.Suppress())
        {
            affected = await bulk(target, cancellationToken);
        }

        List<T> after = delete
            ? []
            : await db.Set<T>().AsNoTracking().IgnoreQueryFilters().Where(filter).ToListAsync(cancellationToken);

        await WriteAuditAsync(db, key, before, after, delete, cancellationToken);

        return affected;
    }

    private static async Task WriteAuditAsync<T>(
        EfAuditTrailDbContext db,
        IKey key,
        List<T> before,
        List<T> after,
        bool delete,
        CancellationToken cancellationToken)
        where T : class
    {
        // A scratch context: the caller's change tracker may hold unrelated pending changes, and the builder
        // audits every Modified/Deleted entry it finds. Same options (model, interceptors), never its own connection.
        var options = (DbContextOptions<EfAuditTrailDbContext>)db.GetService<IDbContextOptions>();
        await using var scratch = new EfAuditTrailDbContext(options, db.Audit);

        var afterByKey = after.ToDictionary(entity => KeyFilter.ValueOf(key, entity), StringComparer.Ordinal);

        foreach (var entity in before)
        {
            var entry = scratch.Attach(entity);

            if (delete)
            {
                entry.State = EntityState.Deleted;
            }
            else if (afterByKey.TryGetValue(KeyFilter.ValueOf(key, entity), out var current))
            {
                // Marks Modified exactly the properties whose value differs — what the interceptor would see.
                entry.CurrentValues.SetValues(current);
            }
        }

        var rows = AuditEntryBuilder.Build(scratch, db.Audit, DateTime.UtcNow);
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.Source = AuditSource.Snapshot;
        }

        // Persist through the caller's connection and transaction, so the audit commits (or rolls back) with
        // the bulk statement. Only AuditEntry rows are tracked now, so the AuditInterceptor adds nothing.
        scratch.ChangeTracker.Clear();
        scratch.Database.SetDbConnection(db.Database.GetDbConnection());
        await scratch.Database.UseTransactionAsync(db.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken);
        scratch.AuditEntries.AddRange(rows);
        await scratch.SaveChangesAsync(cancellationToken);
    }
}
