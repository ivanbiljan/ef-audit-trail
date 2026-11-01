using EfAuditTrail.Api.Features.Shared;
using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Customers;

/// <summary>Moves every customer on one tier to another — bulk, enum stored as text.</summary>
public sealed record UpgradeLoyaltyTier(LoyaltyTier From, LoyaltyTier To, BulkAuditMode Mode) : IRequest<BulkResult>;

[WarpHttpPost("/api/customers/upgrade-tier")]
public sealed class UpgradeLoyaltyTierHandler(EfAuditTrailDbContext db) : IRequestHandler<UpgradeLoyaltyTier, BulkResult>
{
    public async Task<BulkResult> HandleAsync(UpgradeLoyaltyTier request, CancellationToken cancellationToken)
    {
        if (request.From == request.To)
        {
            throw new ValidationException("From and To must differ.");
        }

        var now = DateTime.UtcNow;
        var affected = await request.Mode.UpdateAsync(
            db.Customers.Where(x => x.LoyaltyTier == request.From),
            db,
            s =>
            {
                s.SetProperty(x => x.LoyaltyTier, request.To);
                s.SetProperty(x => x.UpdatedAt, now);
            },
            cancellationToken);

        return new BulkResult(affected, request.Mode);
    }
}
