using EfAuditTrail.Api.Features.Shared;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Products;

/// <summary>Archives every product whose availability has ended. Bulk enum change.</summary>
public sealed record ArchiveExpiredProducts(BulkAuditMode Mode) : IRequest<BulkResult>;

[WarpHttpPost("/api/products/archive-expired")]
public sealed class ArchiveExpiredProductsHandler(EfAuditTrailDbContext db, TimeProvider clock)
    : IRequestHandler<ArchiveExpiredProducts, BulkResult>
{
    public async Task<BulkResult> HandleAsync(ArchiveExpiredProducts request, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var affected = await request.Mode.UpdateAsync(
            db.Products.Where(x => x.AvailableUntil < today && x.Status != ProductStatus.Archived),
            db,
            s => s.SetProperty(x => x.Status, ProductStatus.Archived),
            cancellationToken);

        return new BulkResult(affected, request.Mode);
    }
}
