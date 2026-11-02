using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Orders;

/// <summary>Bulk delete. Each audit row snapshots the whole order, jsonb delivery details included (PII-marked).</summary>
public sealed record PurgeCancelledOrders : IRequest<int>;

[WarpHttpPost("/api/orders/purge-cancelled")]
public sealed class PurgeCancelledOrdersHandler(EfAuditTrailDbContext db) : IRequestHandler<PurgeCancelledOrders, int>
{
    public Task<int> HandleAsync(PurgeCancelledOrders request, CancellationToken cancellationToken)
        => db.Orders.Where(x => x.Status == OrderStatus.Cancelled).ExecuteDeleteAsync(cancellationToken);
}
