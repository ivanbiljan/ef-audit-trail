using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Products;

/// <summary>Bulk price change for a category — one UPDATE, one audit row per product.</summary>
public sealed record AdjustProductPrices(string Category, decimal Percent) : IRequest<int>;

[WarpHttpPost("/api/products/adjust-prices")]
public sealed class AdjustProductPricesHandler(EfAuditTrailDbContext db) : IRequestHandler<AdjustProductPrices, int>
{
    public async Task<int> HandleAsync(AdjustProductPrices request, CancellationToken cancellationToken)
    {
        if (request.Percent is < -50 or > 50 || request.Percent == 0)
        {
            throw new ValidationException("Percent must be non-zero and within ±50.");
        }

        var factor = 1 + (request.Percent / 100m);
        var now = DateTime.UtcNow;

        return await db.Products
            .Where(x => x.Category == request.Category && x.Status != ProductStatus.Archived)
            .ExecuteUpdateAsync(
                s =>
                {
                    s.SetProperty(x => x.Price, x => Math.Round(x.Price * factor, 2));
                    s.SetProperty(x => x.UpdatedAt, now);
                },
                cancellationToken);
    }
}
