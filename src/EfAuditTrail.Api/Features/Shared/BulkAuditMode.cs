using EfAuditTrail.Data;
using EfAuditTrail.Data.Auditing.Snapshots;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace EfAuditTrail.Api.Features.Shared;

/// <summary>Which of the two bulk approaches a playground endpoint should demonstrate.</summary>
public enum BulkAuditMode
{
    /// <summary>Plain EF <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>; the database trigger records the rows.</summary>
    Trigger,

    /// <summary><c>ExecuteAudited*Async</c>: the application snapshots, diffs and writes the rows itself.</summary>
    Snapshot,
}

public sealed record BulkResult(int RowsAffected, BulkAuditMode Mode);

internal static class BulkAuditModeExtensions
{
    public static Task<int> UpdateAsync<T>(
        this BulkAuditMode mode,
        IQueryable<T> query,
        EfAuditTrailDbContext db,
        Action<UpdateSettersBuilder<T>> setters,
        CancellationToken cancellationToken)
        where T : class
        => mode switch
        {
            BulkAuditMode.Trigger => query.ExecuteUpdateAsync(setters, cancellationToken),
            BulkAuditMode.Snapshot => query.ExecuteAuditedUpdateAsync(db, setters, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    public static Task<int> DeleteAsync<T>(
        this BulkAuditMode mode,
        IQueryable<T> query,
        EfAuditTrailDbContext db,
        CancellationToken cancellationToken)
        where T : class
        => mode switch
        {
            BulkAuditMode.Trigger => query.ExecuteDeleteAsync(cancellationToken),
            BulkAuditMode.Snapshot => query.ExecuteAuditedDeleteAsync(db, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
}
