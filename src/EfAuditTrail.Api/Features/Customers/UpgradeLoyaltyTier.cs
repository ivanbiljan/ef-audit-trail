using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Customers;

/// <summary>Moves every customer on one tier to another — bulk, enum stored as text.</summary>
public sealed record UpgradeLoyaltyTier(LoyaltyTier From, LoyaltyTier To) : IRequest<int>;

[WarpHttpPost("/api/customers/upgrade-tier")]
public sealed class UpgradeLoyaltyTierHandler(EfAuditTrailDbContext db) : IRequestHandler<UpgradeLoyaltyTier, int>
{
    public Task<int> HandleAsync(UpgradeLoyaltyTier request, CancellationToken cancellationToken)
    {
        if (request.From == request.To)
        {
            throw new ValidationException("From and To must differ.");
        }

        var now = DateTime.UtcNow;
        return db.Customers
            .Where(x => x.LoyaltyTier == request.From)
            .ExecuteUpdateAsync(
                s =>
                {
                    s.SetProperty(x => x.LoyaltyTier, request.To);
                    s.SetProperty(x => x.UpdatedAt, now);
                },
                cancellationToken);
    }
}
