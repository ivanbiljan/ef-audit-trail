using System.Text.Json.Nodes;
using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Customers;

/// <summary>Edits one branch of the jsonb profile through SaveChanges: the interceptor records leaf paths
/// (<c>Profile/deliveryContact/phone</c>) and marks them personal via <c>[AuditPii("/deliveryContact/**")]</c>.</summary>
public sealed record UpdateDeliveryContact(int CustomerId, string Name, string Phone, string? Language) : IRequest<int>;

[WarpHttpPost("/api/customers/delivery-contact")]
public sealed class UpdateDeliveryContactHandler(EfAuditTrailDbContext db) : IRequestHandler<UpdateDeliveryContact, int>
{
    public async Task<int> HandleAsync(UpdateDeliveryContact request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Phone))
        {
            throw new ValidationException("A delivery contact needs a name and a phone number.");
        }

        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == request.CustomerId, cancellationToken)
            ?? throw new KeyNotFoundException($"Customer {request.CustomerId} does not exist.");

        var profile = JsonNode.Parse(customer.Profile) as JsonObject ?? new JsonObject();
        profile["deliveryContact"] = new JsonObject { ["name"] = request.Name.Trim(), ["phone"] = request.Phone.Trim() };
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            profile["language"] = request.Language.Trim();
        }

        customer.Profile = profile.ToJsonString();

        return await db.SaveChangesAsync(cancellationToken);
    }
}
