using EfAuditTrail.Api.Features.Shared;
using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Products;

/// <summary>Bulk price change for a category — one UPDATE, audited by the trigger or by the snapshot path.</summary>
public sealed record AdjustProductPrices(string Category, decimal Percent, BulkAuditMode Mode) : IRequest<BulkResult>;

[WarpHttpPost("/api/products/adjust-prices")]
public sealed class AdjustProductPricesHandler(EfAuditTrailDbContext db) : IRequestHandler<AdjustProductPrices, BulkResult>
{
    public async Task<BulkResult> HandleAsync(AdjustProductPrices request, CancellationToken cancellationToken)
    {
        if (request.Percent is < -50 or > 50 || request.Percent == 0)
        {
            throw new ValidationException("Percent must be non-zero and within ±50.");
        }

        var factor = 1 + (request.Percent / 100m);
        var now = DateTime.UtcNow;

        var affected = await request.Mode.UpdateAsync(
            db.Products.Where(x => x.Category == request.Category && x.Status != ProductStatus.Archived),
            db,
            s =>
            {
                s.SetProperty(x => x.Price, x => Math.Round(x.Price * factor, 2));
                s.SetProperty(x => x.UpdatedAt, now);
            },
            cancellationToken);

        return new BulkResult(affected, request.Mode);
    }
}
