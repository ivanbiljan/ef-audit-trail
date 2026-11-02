using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Products;

/// <summary>A tracked load-modify-save, audited like any other write.</summary>
public sealed record RenameProduct(int Id, string Name) : IRequest<ProductSummary>;

[WarpHttpPost("/api/products/rename")]
public sealed class RenameProductHandler(EfAuditTrailDbContext db) : IRequestHandler<RenameProduct, ProductSummary>
{
    public async Task<ProductSummary> HandleAsync(RenameProduct request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw new ValidationException("A name is 1–200 characters.");
        }

        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Product {request.Id} does not exist.");

        product.Name = name;
        product.LastReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new ProductSummary(
            product.Id, product.Sku, product.Name, product.Category, product.AvailableUntil, product.Price, product.Status, product.ViewCount);
    }
}
