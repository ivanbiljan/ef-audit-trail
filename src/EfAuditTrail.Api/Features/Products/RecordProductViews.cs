using EfAuditTrail.Data;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Products;

/// <summary>Bumps the [AuditIgnore] view counter for every product — a bulk write that must leave no audit rows.</summary>
public sealed record RecordProductViews : IRequest<int>;

[WarpHttpPost("/api/products/record-views")]
public sealed class RecordProductViewsHandler(EfAuditTrailDbContext db) : IRequestHandler<RecordProductViews, int>
{
    public Task<int> HandleAsync(RecordProductViews request, CancellationToken cancellationToken)
        => db.Products.ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewCount, x => x.ViewCount + 1), cancellationToken);
}
