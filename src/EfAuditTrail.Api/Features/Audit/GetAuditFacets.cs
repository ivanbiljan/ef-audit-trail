using EfAuditTrail.Data;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Audit;

/// <summary>What the feed can be filtered by, with counts — drives the viewer's filter bar and summary tile.</summary>
public sealed record GetAuditFacets : IRequest<AuditFacets>;

public sealed record AuditFacets(
    int Total,
    IReadOnlyList<FacetCount> EntityTypes,
    IReadOnlyList<FacetCount> Operations);

public sealed record FacetCount(string Value, int Count);

[WarpHttpGet("/api/audit/facets")]
public sealed class GetAuditFacetsHandler(EfAuditTrailDbContext db) : IRequestHandler<GetAuditFacets, AuditFacets>
{
    public async Task<AuditFacets> HandleAsync(GetAuditFacets request, CancellationToken cancellationToken)
    {
        var entries = db.AuditEntries.AsNoTracking();

        var entityTypes = await entries
            .GroupBy(x => x.EntityType)
            .OrderBy(g => g.Key)
            .Select(g => new FacetCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var operations = await entries
            .GroupBy(x => x.Operation)
            .OrderBy(g => g.Key)
            .Select(g => new FacetCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        return new AuditFacets(entityTypes.Sum(x => x.Count), entityTypes, operations);
    }
}
