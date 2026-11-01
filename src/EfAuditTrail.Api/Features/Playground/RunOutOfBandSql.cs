using EfAuditTrail.Data;
using EfAuditTrail.Data.Seeding;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Playground;

/// <summary>Runs a fixed UPDATE on a separate connection with no interceptors, as psql would. Only the trigger
/// sees it; the row's "who" is the database role, the channel is <c>Database</c>.</summary>
public sealed record RunOutOfBandSql : IRequest<int>;

[WarpHttpPost("/api/playground/out-of-band")]
public sealed class RunOutOfBandSqlHandler(DemoDatabase database) : IRequestHandler<RunOutOfBandSql, int>
{
    public Task<int> HandleAsync(RunOutOfBandSql request, CancellationToken cancellationToken)
        => OutOfBandSql.ToggleNewsletterAsync(database, cancellationToken);
}
