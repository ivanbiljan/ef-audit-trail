using System.Text.Json;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Auditing.Triggers;
using EfAuditTrail.Data.Entities;
using EfAuditTrail.Data.Seeding;
using EfAuditTrail.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static EfAuditTrail.Tests.Integration.Infrastructure.TestDatabase;

namespace EfAuditTrail.Tests.Integration;

/// <summary>The database trigger is the only thing that writes audit rows, whichever way a change arrives.</summary>
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
        rows.ShouldAllBe(x => x.Action == AuditAction.Update && x.EntityType == "Product");

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
    public async Task SaveChanges_is_recorded_once_with_the_scope_context()
    {
        await _db.AsAsync(Persona.Operations, "Products.Rename", async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Sku == "OUT-02");
            product.Name = "Midnight Sun Sailing II";

            return await db.SaveChangesAsync();
        });

        var row = (await _db.AuditAsync()).ShouldHaveSingleItem();
        row.Operation.ShouldBe("Products.Rename");
        row.ActorDisplay.ShouldBe(Persona.Operations.Display);
        row.Channel.ShouldBe(AuditChannel.BackOffice);
        row.IpAddress.ShouldBe(Persona.Operations.IpAddress);
        Changes(row).Select(x => x.Key).ShouldBe(["Name"]);
    }

    [Fact]
    public async Task SaveChanges_and_a_bulk_statement_in_the_same_transaction_are_both_recorded()
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
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(x => x.Operation == "Mixed");
    }

    [Fact]
    public async Task SaveChanges_against_a_deleted_row_fails_its_concurrency_check_and_writes_no_audit()
    {
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => _db.AsAsync(Persona.Operations, "Products.Rename", async db =>
        {
            var product = await db.Products.SingleAsync(x => x.Sku == "OUT-02");
            await _db.ExecuteOutOfBandAsync("delete from products where sku = 'OUT-02'");
            await _db.ExecuteOutOfBandAsync("delete from audit.entries");
            product.Name = "Gone";

            return await db.SaveChangesAsync();
        }));

        (await _db.AuditAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bulk_statement_can_capture_variables_named_like_the_session_settings()
    {
        var suppress = 7;
        var operation = "Outdoor";

        var affected = await _db.AsAsync(Persona.Pricing, "Products.Restock", db =>
            db.Products.Where(x => x.Category == operation).ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, suppress)));

        affected.ShouldBe(3);
        var rows = await _db.AuditAsync();
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(x => x.Operation == "Products.Restock");
    }

    [Fact]
    public async Task A_bulk_statement_that_matches_nothing_reports_zero_rows()
    {
        var affected = await _db.AsAsync(Persona.Pricing, "Products.Restock", db =>
            db.Products.Where(x => x.Category == "Atlantis").ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 1)));

        affected.ShouldBe(0);
    }

    [Fact]
    public async Task Raw_sql_that_cannot_run_in_a_transaction_still_works()
    {
        await _db.AsAsync(Persona.Operations, "Maintenance", db => db.Database.ExecuteSqlRawAsync("vacuum products"));
    }

    [Fact]
    public async Task SaveChanges_with_a_stale_row_in_a_batch_fails_and_keeps_context_for_the_rows_that_committed()
    {
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => _db.AsAsync(Persona.Operations, "Products.Rename", async db =>
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            var products = await db.Products.Where(x => x.Category == "Outdoor").OrderBy(x => x.Id).Take(2).ToListAsync();
            await _db.ExecuteOutOfBandAsync($"delete from products where id = {products[0].Id}");
            await _db.ExecuteOutOfBandAsync("delete from audit.entries");
            products[0].Name = "Gone";
            products[1].Name = "Still here";

            return await db.SaveChangesAsync();
        }));

        var row = (await _db.AuditAsync()).ShouldHaveSingleItem();
        row.Operation.ShouldBe("Products.Rename");
        row.ActorDisplay.ShouldBe(Persona.Operations.Display);
    }

    [Fact]
    public async Task Without_a_channel_a_save_or_bulk_write_fails_rather_than_passing_as_out_of_band()
    {
        await using var scope = _db.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>();
        var product = await db.Products.SingleAsync(x => x.Sku == "OUT-02");
        product.Name = "No channel";

        // SaveChanges wraps what is thrown while it executes.
        (await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException.ShouldBeOfType<InvalidOperationException>();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            db.Products.Where(x => x.Sku == "OUT-02").ExecuteUpdateAsync(s => s.SetProperty(x => x.Stock, 1)));

        (await _db.AuditAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_an_operation_the_row_is_unattributed()
    {
        await using var scope = _db.Services.CreateAsyncScope();
        Persona.Operations.ApplyTo(scope.ServiceProvider.GetRequiredService<AuditContext>());
        var db = scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>();
        (await db.Products.SingleAsync(x => x.Sku == "OUT-02")).Name = "No operation";
        await db.SaveChangesAsync();

        (await _db.AuditAsync()).ShouldHaveSingleItem().Operation.ShouldBe("unattributed");
    }

    [Fact]
    public async Task Inserts_are_not_audited()
    {
        await _db.AsAsync(Persona.Operations, "Companies.Add", async db =>
        {
            db.Companies.Add(new Company { Name = "New Co" });

            return await db.SaveChangesAsync();
        });

        (await _db.AuditAsync()).ShouldBeEmpty();
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
    public async Task An_fk_cascade_is_recorded_along_with_the_parent_delete()
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
        rows.Single(x => x.EntityType == "Product").Action.ShouldBe(AuditAction.Delete);
        var cascaded = rows.Where(x => x.EntityType == "Order").ToList();
        cascaded.Count.ShouldBe(orders);
        cascaded.ShouldAllBe(x => x.Action == AuditAction.Delete && x.Operation == "Products.Delete");
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
