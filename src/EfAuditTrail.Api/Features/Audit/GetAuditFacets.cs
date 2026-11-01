using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Audit;

/// <summary>What the feed can be filtered by, with counts — drives the viewer's filter bar and summary tiles.</summary>
public sealed record GetAuditFacets : IRequest<AuditFacets>;

public sealed record AuditFacets(
    int Total,
    IReadOnlyList<FacetCount> EntityTypes,
    IReadOnlyList<FacetCount> Sources,
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

        var sources = await entries
            .GroupBy(x => x.Source)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var operations = await entries
            .GroupBy(x => x.Operation)
            .OrderBy(g => g.Key)
            .Select(g => new FacetCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        // Every source, even the ones with no rows yet, so the viewer can show a zero tile.
        var bySource = Enum.GetValues<AuditSource>()
            .Select(s => new FacetCount(s.ToString(), sources.FirstOrDefault(x => x.Key == s)?.Count ?? 0))
            .ToList();

        return new AuditFacets(entityTypes.Sum(x => x.Count), entityTypes, bySource, operations);
    }
}
