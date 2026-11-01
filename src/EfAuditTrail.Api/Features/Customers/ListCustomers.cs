using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Customers;

public sealed record ListCustomers : IRequest<IReadOnlyList<CustomerSummary>>;

public sealed record CustomerSummary(int Id, string FullName, LoyaltyTier LoyaltyTier);

[WarpHttpGet("/api/customers")]
public sealed class ListCustomersHandler(EfAuditTrailDbContext db) : IRequestHandler<ListCustomers, IReadOnlyList<CustomerSummary>>
{
    public async Task<IReadOnlyList<CustomerSummary>> HandleAsync(ListCustomers request, CancellationToken cancellationToken)
        => await db.Customers
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new CustomerSummary(x.Id, x.FullName, x.LoyaltyTier))
            .ToListAsync(cancellationToken);
}
