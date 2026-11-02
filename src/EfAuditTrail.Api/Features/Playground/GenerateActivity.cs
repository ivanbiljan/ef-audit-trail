using EfAuditTrail.Data.Seeding;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Playground;

/// <summary>Replays the migrator's burst of activity — every kind of write, several personas.
/// Each step runs as its own persona, so the caller's headers do not apply here.</summary>
public sealed record GenerateActivity : IRequest<IReadOnlyList<ActivityStep>>;

[WarpHttpPost("/api/playground/activity")]
public sealed class GenerateActivityHandler(ActivityGenerator generator) : IRequestHandler<GenerateActivity, IReadOnlyList<ActivityStep>>
{
    public Task<IReadOnlyList<ActivityStep>> HandleAsync(GenerateActivity request, CancellationToken cancellationToken)
        => generator.RunAsync(cancellationToken);
}
