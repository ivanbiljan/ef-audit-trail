using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Products;

public sealed record ListProducts : IRequest<IReadOnlyList<ProductSummary>>;

public sealed record ProductSummary(
    int Id,
    string Sku,
    string Name,
    string Category,
    DateOnly AvailableUntil,
    decimal Price,
    ProductStatus Status,
    int ViewCount);

[WarpHttpGet("/api/products")]
public sealed class ListProductsHandler(EfAuditTrailDbContext db) : IRequestHandler<ListProducts, IReadOnlyList<ProductSummary>>
{
    public async Task<IReadOnlyList<ProductSummary>> HandleAsync(ListProducts request, CancellationToken cancellationToken)
        => await db.Products
            .AsNoTracking()
            .OrderBy(x => x.Category).ThenBy(x => x.Sku)
            .Select(x => new ProductSummary(x.Id, x.Sku, x.Name, x.Category, x.AvailableUntil, x.Price, x.Status, x.ViewCount))
            .ToListAsync(cancellationToken);
}
