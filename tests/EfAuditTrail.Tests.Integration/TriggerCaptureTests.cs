using System.Text.Json;
using EfAuditTrail.Data.Auditing.Triggers;
using EfAuditTrail.Data.Entities;
using EfAuditTrail.Data.Seeding;
using EfAuditTrail.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static EfAuditTrail.Tests.Integration.Infrastructure.TestDatabase;

namespace EfAuditTrail.Tests.Integration;

/// <summary>Approach 1: the database trigger records what the interceptor cannot see.</summary>
public sealed class TriggerCaptureTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await CreateAsync();

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task ExecuteUpdate_is_recorded_once_per_row_with_the_scope_context()
    {
        var affected = await _db.AsAsync(Persona.Pricing, "Products.AdjustPrices", db =>
            db.Products.Where(x => x.Category == "Outdoor").ExecuteUpdateAsync(s =>
            {
                s.SetProperty(x => x.Price, x => x.Price + 100);
                s.SetProperty(x => x.UpdatedAt, DateTime.UtcNow);
            }));

        var rows = await _db.AuditAsync();

        affected.ShouldBe(3);
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(x => x.Source == AuditSource.Trigger && x.Action == AuditAction.Update && x.EntityType == "Product");

        var row = rows[0];
        row.Operation.ShouldBe("Products.AdjustPrices");
        row.ActorKind.ShouldBe(AuditActorKind.Staff);
        row.ActorId.ShouldBe(12);
        row.ActorDisplay.ShouldBe(Persona.Pricing.Display);
        row.Channel.ShouldBe(AuditChannel.BackOffice);
        row.IpAddress.ShouldBe(Persona.Pricing.IpAddress);
        row.UserAgent.ShouldBe(Persona.Pricing.UserAgent);
        row.CompanyId.ShouldBe(1);

        var price = Changes(row).ShouldHaveSingleItem();
        price.Key.ShouldBe("Price");
        (price.Value!["new"]!.GetValue<decimal>() - price.Value!["old"]!.GetValue<decimal>()).ShouldBe(100m);
    }

    [Fact]
    public async Task SaveChanges_is_recorded_by_the_interceptor_and_not_again_by_the_trigger()
    {
        await _db.AsAsync(Persona.Operations, "Products.Rename", async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Sku == "OUT-02");
            product.Name = "Midnight Sun Sailing II";

            return await db.SaveChangesAsync();
        });

        var row = (await _db.AuditAsync()).ShouldHaveSingleItem();
        row.Source.ShouldBe(AuditSource.Interceptor);
        Changes(row).Select(x => x.Key).ShouldBe(["Name"]);
    }

    [Fact]
    public async Task Suppression_for_SaveChanges_does_not_leak_into_a_later_bulk_statement_in_the_same_transaction()
    {
        await _db.AsAsync(Persona.Operations, "Mixed", async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            var product = await db.Products.SingleAsync(x => x.Sku == "OFF-01");
            product.Name = "Standing Desk XL";
            await db.SaveChangesAsync();

            await db.Products.Where(x => x.Category == "Kitchen")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, x => x.Stock + 2));

            await transaction.CommitAsync();

            return 0;
        });

        var rows = await _db.AuditAsync();
        rows.Count(x => x.Source == AuditSource.Interceptor).ShouldBe(1);
        rows.Count(x => x.Source == AuditSource.Trigger).ShouldBe(2);
    }

    [Fact]
    public async Task A_rolled_back_bulk_statement_leaves_no_audit()
    {
        await _db.AsAsync(Persona.Pricing, "Products.AdjustPrices", async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Products.ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 1m));
            await transaction.RollbackAsync();

            return 0;
        });

        (await _db.AuditAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Updates_that_only_touch_ignored_columns_are_not_recorded()
    {
        var affected = await _db.AsAsync(Persona.Pricing, "Products.RecordViews", db =>
            db.Products.ExecuteUpdateAsync(s =>
            {
                s.SetProperty(x => x.ViewCount, x => x.ViewCount + 1);
                s.SetProperty(x => x.UpdatedAt, DateTime.UtcNow);
            }));

        affected.ShouldBe(9);
        (await _db.AuditAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteDelete_snapshots_the_whole_row_and_marks_the_delivery_details_personal()
    {
        var affected = await _db.AsAsync(Persona.NightlyJob, "Maintenance.Purge", db =>
            db.Orders.Where(x => x.Status == OrderStatus.Cancelled).ExecuteDeleteAsync());

        var rows = await _db.AuditAsync();

        affected.ShouldBeGreaterThan(0);
        rows.Count.ShouldBe(affected);

        var row = rows[0];
        row.Action.ShouldBe(AuditAction.Delete);
        row.Channel.ShouldBe(AuditChannel.Job);
        row.ActorKind.ShouldBe(AuditActorKind.System);

        var changes = Changes(row);
        changes.Select(x => x.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["CustomerId", "Delivery", "ProductId", "Quantity", "Status", "TotalPrice"]);
        changes.ShouldAllBe(x => x.Value!["new"] == null);
        changes["Status"]!["old"]!.GetValue<string>().ShouldBe("Cancelled");
        PiiPaths(row).ShouldBe(["Delivery"]);
    }

    [Fact]
    public async Task Out_of_band_sql_is_attributed_to_the_database_role()
    {
        await _db.ExecuteOutOfBandAsync("update products set name = name || ' (dba)' where sku = 'OFF-02'");

        var row = (await _db.AuditAsync()).ShouldHaveSingleItem();
        row.Source.ShouldBe(AuditSource.Trigger);
        row.Channel.ShouldBe(AuditChannel.Database);
        row.ActorKind.ShouldBe(AuditActorKind.System);
        row.ActorDisplay.ShouldBe("postgres");
        row.Operation.ShouldBe("unattributed");
        row.IpAddress.ShouldBeNull();
    }

    [Fact]
    public async Task Jsonb_changes_are_diffed_to_leaves_and_only_personal_leaves_are_listed()
    {
        await _db.ExecuteOutOfBandAsync("""
            update customers
               set profile = jsonb_set(jsonb_set(profile, '{deliveryContact,phone}', '"+1 555 0000"'), '{language}', '"fr"')
             where id = (select min(id) from customers)
            """);

        var row = (await _db.AuditAsync()).ShouldHaveSingleItem();

        Changes(row).Select(x => x.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["Profile/deliveryContact/phone", "Profile/language"]);
        PiiPaths(row).ShouldBe(["Profile/deliveryContact/phone"]);
    }

    [Fact]
    public async Task Timestamps_are_formatted_exactly_like_System_Text_Json()
    {
        var reviewed = new DateTime(2026, 11, 1, 10, 0, 0, 500, DateTimeKind.Utc);

        await _db.AsAsync(Persona.Operations, "Products.Review", db =>
            db.Products.Where(x => x.Sku == "OUT-01").ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReviewedAt, reviewed)));

        var change = Changes((await _db.AuditAsync()).Single())["LastReviewedAt"]!;
        change["new"]!.GetValue<string>().ShouldBe(JsonSerializer.SerializeToNode(reviewed)!.GetValue<string>());
        change["old"].ShouldBeNull();
    }

    [Fact]
    public async Task Raw_sql_through_ef_carries_the_scope_context()
    {
        await _db.AsAsync(Persona.Pricing, "Suppliers.Raise", db =>
            db.Database.ExecuteSqlAsync($"update suppliers set commission_rate = commission_rate + 1 where country = {"Germany"}"));

        var row = (await _db.AuditAsync()).ShouldHaveSingleItem();
        row.EntityType.ShouldBe("Supplier");
        row.ActorDisplay.ShouldBe(Persona.Pricing.Display);
        row.Operation.ShouldBe("Suppliers.Raise");
    }

    [Fact]
    public async Task An_fk_cascade_is_recorded_even_when_the_parent_delete_was_audited_by_the_app()
    {
        var orders = await _db.ReadAsync(db => db.Orders.CountAsync(x => db.Products.Any(e => e.Id == x.ProductId && e.Sku == "GAR-01")));

        await _db.AsAsync(Persona.Operations, "Products.Delete", async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Sku == "GAR-01");
            db.Products.Remove(product);

            return await db.SaveChangesAsync();
        });

        var rows = await _db.AuditAsync();

        orders.ShouldBeGreaterThan(0);
        rows.Single(x => x.EntityType == "Product").Source.ShouldBe(AuditSource.Interceptor);
        var cascaded = rows.Where(x => x.EntityType == "Order").ToList();
        cascaded.Count.ShouldBe(orders);
        cascaded.ShouldAllBe(x => x.Source == AuditSource.Trigger && x.Action == AuditAction.Delete && x.Operation == "Products.Delete");
    }

    [Fact]
    public async Task Every_audited_table_has_the_trigger_and_nothing_else_is_left_untracked()
    {
        (await _db.ScalarAsync<long>("select count(*) from audit.untracked_tables")).ShouldBe(0);
        (await _db.ScalarAsync<long>("select count(*) from pg_trigger where tgname = 'zz_audit_capture'")).ShouldBe(5);
    }

    [Fact]
    public async Task Installing_twice_is_harmless()
    {
        await _db.AsAsync(Persona.Seeder, "tests.reinstall", async db =>
        {
            await AuditTriggerInstaller.InstallAsync(db);

            return 0;
        });

        await _db.ExecuteOutOfBandAsync("update suppliers set active = not active where id = (select min(id) from suppliers)");

        (await _db.AuditAsync()).ShouldHaveSingleItem();
    }
}
