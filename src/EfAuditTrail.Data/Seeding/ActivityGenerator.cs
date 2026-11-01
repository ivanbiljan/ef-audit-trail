using System.Text.Json.Nodes;
using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Auditing.Snapshots;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EfAuditTrail.Data.Seeding;

/// <summary>Who did it. Hosts map their own principal onto this; the demo just names a few people.</summary>
public sealed record Persona(
    AuditActorKind Kind,
    int? Id,
    string Display,
    AuditChannel Channel,
    string? IpAddress = null,
    string? UserAgent = null,
    int? OnBehalfOfCustomerId = null)
{
    public static readonly Persona Operations = new(AuditActorKind.Staff, 7, "Alex Morgan (ops)", AuditChannel.BackOffice, "10.20.0.17", "Mozilla/5.0 (Macintosh) BackOffice");
    public static readonly Persona Pricing = new(AuditActorKind.Staff, 12, "Sam Lee (pricing)", AuditChannel.BackOffice, "10.20.0.31", "Mozilla/5.0 (Windows NT 10.0) BackOffice");
    public static readonly Persona Shopper = new(AuditActorKind.Customer, 1, "Avery Johnson", AuditChannel.Api, "203.0.113.24", "DemoApp-iOS/4.2");
    public static readonly Persona NightlyJob = new(AuditActorKind.System, null, "nightly-maintenance", AuditChannel.Job);
    public static readonly Persona Seeder = new(AuditActorKind.System, null, "migrator", AuditChannel.Seeder);

    public static Persona SupportOnBehalfOf(int customerId)
        => new(AuditActorKind.Staff, 21, "Jordan Kim (support)", AuditChannel.BackOffice, "10.20.0.44", "Mozilla/5.0 (X11; Linux) BackOffice", customerId);

    public void ApplyTo(AuditContext context)
    {
        context.ActorKind = Kind;
        context.ActorId = Id;
        context.ActorDisplay = Display;
        context.Channel = Channel;
        context.IpAddress = IpAddress;
        context.UserAgent = UserAgent;
        context.OnBehalfOfCustomerId = OnBehalfOfCustomerId;
    }
}

public sealed record ActivityStep(string Operation, AuditSource ExpectedSource, int RowsAffected);

/// <summary>
/// Replays a believable slice of a day through every capture path, each step as a different person in its
/// own scope, so the audit feed has something to compare:
/// SaveChanges (interceptor), plain ExecuteUpdate/ExecuteDelete (trigger), ExecuteAuditedUpdate/Delete
/// (snapshot) and raw SQL from outside the application (trigger, no app context).
/// </summary>
public sealed class ActivityGenerator(IServiceScopeFactory scopes, DemoDatabase database)
{
    public async Task<IReadOnlyList<ActivityStep>> RunAsync(CancellationToken cancellationToken = default)
    {
        var steps = new List<ActivityStep>();
        var random = Random.Shared;

        // 1. Ops renames a product and reworks one of its specs — SaveChanges → interceptor.
        steps.Add(await AsAsync(Persona.Operations, "Products.ReworkAttributes", AuditSource.Interceptor, async db =>
        {
            var product = await db.Products.OrderBy(x => x.Id).Skip(random.Next(0, 9)).FirstAsync(cancellationToken);
            var attributes = JsonNode.Parse(product.Attributes)!.AsObject();
            attributes["specs"]![1]!["value"] = Pick(random, "3 years", "5 years", "Lifetime");
            attributes["tags"]!.AsArray().Add(Pick(random, "eco", "limited", "staff-pick"));
            product.Attributes = attributes.ToJsonString();
            product.Name = Pick(random, "Trail Backpack 40L", "Trail Backpack 40L Pro", "Trail Pack 40");
            product.LastReviewedAt = DateTime.UtcNow;

            return await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken));

        // 2. A customer updates their delivery contact and language in the app — SaveChanges, jsonb, PII.
        steps.Add(await AsAsync(Persona.Shopper, "Customers.UpdateProfile", AuditSource.Interceptor, async db =>
        {
            var customer = await db.Customers.OrderBy(x => x.Id).FirstAsync(cancellationToken);
            var profile = JsonNode.Parse(customer.Profile)!.AsObject();
            profile["deliveryContact"]!["phone"] = $"+1 555 04{random.Next(10, 99)}";
            profile["language"] = Pick(random, "fr", "es", "en");
            customer.Profile = profile.ToJsonString();
            customer.Phone = $"+1 555 05{random.Next(10, 99)}";

            return await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken));

        // 3. Support changes an email on a customer's behalf — SaveChanges, PII, on-behalf-of.
        var supportTarget = await WithDbAsync(db => db.Customers.OrderBy(x => x.Id).Skip(1).Select(x => x.Id).FirstAsync(cancellationToken), cancellationToken);
        steps.Add(await AsAsync(Persona.SupportOnBehalfOf(supportTarget), "Customers.ChangeEmail", AuditSource.Interceptor, async db =>
        {
            var customer = await db.Customers.SingleAsync(x => x.Id == supportTarget, cancellationToken);
            customer.Email = $"{customer.FullName.Split(' ')[0].ToLowerInvariant()}.{random.Next(1000, 9999)}@example.org";

            return await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken));

        // 4. Ops takes three new orders and cancels two of them — inserts are not audited, the cancellations are.
        steps.Add(await AsAsync(Persona.Operations, "Orders.Cancel", AuditSource.Interceptor, async db =>
        {
            var product = await db.Products.Where(x => x.Status == ProductStatus.Active).OrderBy(x => x.Id).FirstAsync(cancellationToken);
            var customers = await db.Customers.OrderBy(x => x.Id).Take(3).ToListAsync(cancellationToken);
            var fresh = customers.Select((c, i) => new Order
            {
                CompanyId = product.CompanyId,
                ProductId = product.Id,
                CustomerId = c.Id,
                Quantity = 1 + i,
                Status = OrderStatus.Pending,
                TotalPrice = product.Price * (1 + i),
                Delivery = DemoSeeder.Delivery(c.FullName, 1 + i, random.Next(10, 99)),
            }).ToList();
            db.Orders.AddRange(fresh);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var order in fresh.Take(2))
            {
                order.Status = OrderStatus.Cancelled;
            }

            return await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken));

        // 5. Pricing raises one category with a plain ExecuteUpdate — invisible to the interceptor, caught by the trigger.
        var triggerCategory = Pick(random, DemoSeeder.Categories);
        steps.Add(await AsAsync(Persona.Pricing, "Products.AdjustPrices", AuditSource.Trigger, db =>
        {
            var now = DateTime.UtcNow;

            return db.Products
                .Where(x => x.Category == triggerCategory && x.Status != ProductStatus.Archived)
                .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(x => x.Price, x => Math.Round(x.Price * 1.04m, 2));
                    s.SetProperty(x => x.UpdatedAt, now);
                }, cancellationToken);
        }, cancellationToken));

        // 6. …and another category through the snapshot path — same rows, written by the application.
        var snapshotCategory = DemoSeeder.Categories.First(x => x != triggerCategory);
        steps.Add(await AsAsync(Persona.Pricing, "Products.AdjustPrices", AuditSource.Snapshot, db =>
        {
            var now = DateTime.UtcNow;

            return db.Products
                .Where(x => x.Category == snapshotCategory && x.Status != ProductStatus.Archived)
                .ExecuteAuditedUpdateAsync(db, s =>
                {
                    s.SetProperty(x => x.Price, x => Math.Round(x.Price * 0.97m, 2));
                    s.SetProperty(x => x.UpdatedAt, now);
                }, cancellationToken);
        }, cancellationToken));

        // 7. The nightly job archives expired products (trigger) and purges cancelled orders (snapshot delete).
        steps.Add(await AsAsync(Persona.NightlyJob, "Maintenance.ArchiveExpired", AuditSource.Trigger, db =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            return db.Products
                .Where(x => x.AvailableUntil < today && x.Status != ProductStatus.Archived)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ProductStatus.Archived), cancellationToken);
        }, cancellationToken));

        steps.Add(await AsAsync(Persona.NightlyJob, "Maintenance.PurgeCancelledOrders", AuditSource.Snapshot, db =>
            db.Orders.Where(x => x.Status == OrderStatus.Cancelled).ExecuteAuditedDeleteAsync(db, cancellationToken), cancellationToken));

        // 8. Loyalty upgrade with a plain ExecuteUpdate — trigger, enum column.
        steps.Add(await AsAsync(Persona.Pricing, "Customers.UpgradeLoyalty", AuditSource.Trigger, db =>
            db.Customers
                .Where(x => x.LoyaltyTier == LoyaltyTier.Silver)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LoyaltyTier, LoyaltyTier.Gold), cancellationToken), cancellationToken));

        // 9. A DBA flips a newsletter flag straight from psql — no application, no context: only the trigger sees it.
        steps.Add(new ActivityStep("(out of band)", AuditSource.Trigger, await OutOfBandSql.ToggleNewsletterAsync(database, cancellationToken)));

        return steps;
    }

    private async Task<ActivityStep> AsAsync(
        Persona persona,
        string operation,
        AuditSource expected,
        Func<EfAuditTrailDbContext, Task<int>> work,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditContext>();
        persona.ApplyTo(context);

        using (context.BeginOperation(operation))
        {
            var db = scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>();
            cancellationToken.ThrowIfCancellationRequested();

            return new ActivityStep(operation, expected, await work(db));
        }
    }

    private async Task<T> WithDbAsync<T>(Func<EfAuditTrailDbContext, Task<T>> read, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        cancellationToken.ThrowIfCancellationRequested();

        return await read(scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>());
    }

    private static T Pick<T>(Random random, params T[] options) => options[random.Next(options.Length)];
}

/// <summary>Writes that bypass the application entirely — a separate connection with no interceptors, as a
/// DBA in psql or another service would. Fixed statements only: the playground never runs arbitrary SQL.</summary>
public static class OutOfBandSql
{
    public static async Task<int> ToggleNewsletterAsync(DemoDatabase database, CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = "dba-console" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            update customers
               set profile = jsonb_set(profile, '{newsletter}', to_jsonb(not coalesce((profile ->> 'newsletter')::boolean, false))),
                   marketing_opt_in = not marketing_opt_in
             where id = (select min(id) from customers)
            """;

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
