using EfAuditTrail.Api.Features.Shared;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Orders;

/// <summary>Bulk delete. Each audit row snapshots the whole order, jsonb delivery details included (PII-marked).</summary>
public sealed record PurgeCancelledOrders(BulkAuditMode Mode) : IRequest<BulkResult>;

[WarpHttpPost("/api/orders/purge-cancelled")]
public sealed class PurgeCancelledOrdersHandler(EfAuditTrailDbContext db) : IRequestHandler<PurgeCancelledOrders, BulkResult>
{
    public async Task<BulkResult> HandleAsync(PurgeCancelledOrders request, CancellationToken cancellationToken)
    {
        var affected = await request.Mode.DeleteAsync(
            db.Orders.Where(x => x.Status == OrderStatus.Cancelled),
            db,
            cancellationToken);

        return new BulkResult(affected, request.Mode);
    }
}
