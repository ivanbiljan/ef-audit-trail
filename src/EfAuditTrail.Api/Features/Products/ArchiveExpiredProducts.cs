using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Products;

/// <summary>Archives every product whose availability has ended. Bulk enum change.</summary>
public sealed record ArchiveExpiredProducts : IRequest<int>;

[WarpHttpPost("/api/products/archive-expired")]
public sealed class ArchiveExpiredProductsHandler(EfAuditTrailDbContext db, TimeProvider clock)
    : IRequestHandler<ArchiveExpiredProducts, int>
{
    public Task<int> HandleAsync(ArchiveExpiredProducts request, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        return db.Products
            .Where(x => x.AvailableUntil < today && x.Status != ProductStatus.Archived)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProductStatus.Archived), cancellationToken);
    }
}
