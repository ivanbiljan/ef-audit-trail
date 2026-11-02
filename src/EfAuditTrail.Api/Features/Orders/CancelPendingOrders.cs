using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Orders;

/// <summary>Cancels the pending orders of a product (or all) — gives "purge cancelled" something to delete.</summary>
public sealed record CancelPendingOrders(int? ProductId) : IRequest<int>;

[WarpHttpPost("/api/orders/cancel-pending")]
public sealed class CancelPendingOrdersHandler(EfAuditTrailDbContext db) : IRequestHandler<CancelPendingOrders, int>
{
    public Task<int> HandleAsync(CancelPendingOrders request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var pending = db.Orders.Where(x => x.Status == OrderStatus.Pending);
        if (request.ProductId is { } productId)
        {
            pending = pending.Where(x => x.ProductId == productId);
        }

        return pending.ExecuteUpdateAsync(
            s =>
            {
                s.SetProperty(x => x.Status, OrderStatus.Cancelled);
                s.SetProperty(x => x.UpdatedAt, now);
            },
            cancellationToken);
    }
}
