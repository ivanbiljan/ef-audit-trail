using EfAuditTrail.Data.Auditing;
using Warp.Core.Handlers;

namespace EfAuditTrail.Api.Infrastructure;

/// <summary>
/// Pushes the operation code — the "what were they doing" of an audit row — for the duration of every Warp
/// request, so every write a handler makes, through any capture path, carries it. Open generic: Warp's source
/// generator registers it for every request type.
/// </summary>
public sealed class AuditOperationBehavior<TRequest, TResponse>(AuditContext audit) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        using var operation = audit.BeginOperation(OperationCode.For(typeof(TRequest)));

        return await next(request, cancellationToken);
    }
}

public static class OperationCode
{
    /// <summary><c>EfAuditTrail.Api.Features.Products.AdjustProductPrices</c> → <c>Products.AdjustProductPrices</c>.</summary>
    public static string For(Type requestType)
    {
        var feature = requestType.Namespace?.Split('.').LastOrDefault();

        return string.IsNullOrEmpty(feature) ? requestType.Name : $"{feature}.{requestType.Name}";
    }
}
