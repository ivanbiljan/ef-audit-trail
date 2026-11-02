using EfAuditTrail.Data.Entities;
using EfAuditTrail.Data.Seeding;
using EfAuditTrail.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static EfAuditTrail.Tests.Integration.Infrastructure.TestDatabase;

namespace EfAuditTrail.Tests.Integration;

/// <summary>Volumes large enough to cross EF's batch boundary, so a SaveChanges spans several commands.</summary>
public sealed class BulkFlowTests : IAsyncLifetime
{
    private const int Rows = 2_500;

    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await CreateAsync();
        await _db.ExecuteOutOfBandAsync($$"""
            insert into products (company_id, sku, name, category, available_until, price, stock, status, attributes, view_count, created_at, updated_at)
            select 1, 'BULK-' || n, 'Bulk ' || n, 'Bulk', date '2030-01-01', 10, 1, 'Active', '{"specs": [{"name": "a", "value": "1"}], "tags": []}', 0, now(), now()
              from generate_series(1, {{Rows}}) as n
            """);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task A_large_SaveChanges_records_every_row_with_the_scope_context()
    {
        var saved = await _db.AsAsync(Persona.Pricing, "Products.Reprice", async db =>
        {
            var products = await db.Products.AsTracking().Where(x => x.Category == "Bulk").ToListAsync();
            products.ForEach(x => x.Price = 11);

            return await db.SaveChangesAsync();
        });

        var rows = await _db.AuditAsync();

        saved.ShouldBe(Rows);
        rows.Count.ShouldBe(Rows);
        rows.Select(x => x.EntityKey).Distinct().Count().ShouldBe(Rows);
        rows.ShouldAllBe(x => x.Operation == "Products.Reprice" && x.ActorId == 12 && x.Channel == AuditChannel.BackOffice);
    }

    [Fact]
    public async Task A_stale_row_late_in_a_large_SaveChanges_rolls_back_every_row_and_every_audit_row()
    {
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => _db.AsAsync(Persona.Pricing, "Products.Reprice", async db =>
        {
            var products = await db.Products.AsTracking().Where(x => x.Category == "Bulk").OrderBy(x => x.Id).ToListAsync();
            await _db.ExecuteOutOfBandAsync($"delete from products where id = {products[Rows - 100].Id}");
            await _db.ExecuteOutOfBandAsync("delete from audit.entries");
            products.ForEach(x => x.Price = 11);

            return await db.SaveChangesAsync();
        }));

        (await _db.AuditAsync()).ShouldBeEmpty();
        (await _db.ReadAsync(db => db.Products.CountAsync(x => x.Category == "Bulk" && x.Price == 11))).ShouldBe(0);
    }

    [Fact]
    public async Task A_large_tracked_delete_records_every_row()
    {
        var deleted = await _db.AsAsync(Persona.NightlyJob, "Products.Purge", async db =>
        {
            db.Products.RemoveRange(await db.Products.AsTracking().Where(x => x.Category == "Bulk").ToListAsync());

            return await db.SaveChangesAsync();
        });

        var rows = await _db.AuditAsync();

        deleted.ShouldBe(Rows);
        rows.Count.ShouldBe(Rows);
        rows.ShouldAllBe(x => x.Action == AuditAction.Delete && x.Operation == "Products.Purge");
    }

    [Fact]
    public async Task A_large_bulk_update_records_every_row_with_the_scope_context()
    {
        var updated = await _db.AsAsync(Persona.Pricing, "Products.Reprice", db =>
            db.Products.Where(x => x.Category == "Bulk").ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 12m)));

        var rows = await _db.AuditAsync();

        updated.ShouldBe(Rows);
        rows.Count.ShouldBe(Rows);
        rows.ShouldAllBe(x => x.Operation == "Products.Reprice" && x.ActorId == 12);
    }
}
