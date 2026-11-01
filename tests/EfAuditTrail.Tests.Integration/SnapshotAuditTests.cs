using System.Collections.Concurrent;
using System.Data.Common;
using EfAuditTrail.Data.Auditing.Snapshots;
using EfAuditTrail.Data.Entities;
using EfAuditTrail.Data.Seeding;
using EfAuditTrail.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static EfAuditTrail.Tests.Integration.Infrastructure.TestDatabase;

namespace EfAuditTrail.Tests.Integration;

/// <summary>Approach 2: ExecuteAudited* — lock, snapshot before, run, snapshot after, diff through the builder.</summary>
public sealed class SnapshotAuditTests : IAsyncLifetime
{
    private readonly CommandLog _log = new();
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await CreateAsync(options => options.AddInterceptors(_log));

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task An_audited_update_writes_snapshot_rows_and_the_trigger_stands_down()
    {
        var affected = await _db.AsAsync(Persona.Pricing, "Products.AdjustPrices", db =>
            db.Products.Where(x => x.Category == "Outdoor").ExecuteAuditedUpdateAsync(db, s =>
            {
                s.SetProperty(x => x.Price, x => x.Price + 100);
                s.SetProperty(x => x.UpdatedAt, DateTime.UtcNow);
            }));

        var rows = await _db.AuditAsync();

        affected.ShouldBe(3);
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(x => x.Source == AuditSource.Snapshot && x.Action == AuditAction.Update);
        rows.ShouldAllBe(x => x.Operation == "Products.AdjustPrices" && x.ActorId == 12 && x.Channel == AuditChannel.BackOffice);
        rows.Select(x => Changes(x).Single().Key).Distinct().ShouldBe(["Price"]);
    }

    [Fact]
    public async Task Rows_the_statement_did_not_actually_change_are_not_recorded()
    {
        // Outdoor: two Open, one Draft. Setting Open matches three rows and changes one.
        var affected = await _db.AsAsync(Persona.Operations, "Products.Open", db =>
            db.Products.Where(x => x.Category == "Outdoor")
                .ExecuteAuditedUpdateAsync(db, s => s.SetProperty(x => x.Status, ProductStatus.Active)));

        affected.ShouldBe(3);
        var row = (await _db.AuditAsync()).ShouldHaveSingleItem();
        row.EntityKey.ShouldBe((await _db.ReadAsync(db => db.Products.SingleAsync(x => x.Sku == "OUT-03"))).Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Changes(row)["Status"]!["old"]!.GetValue<string>().ShouldBe("Draft");
    }

    [Fact]
    public async Task An_audited_delete_snapshots_each_row()
    {
        var cancelled = await _db.ReadAsync(db => db.Orders.CountAsync(x => x.Status == OrderStatus.Cancelled));

        var affected = await _db.AsAsync(Persona.NightlyJob, "Maintenance.Purge", db =>
            db.Orders.Where(x => x.Status == OrderStatus.Cancelled).ExecuteAuditedDeleteAsync(db));

        var rows = await _db.AuditAsync();

        affected.ShouldBe(cancelled);
        rows.Count.ShouldBe(cancelled);
        rows.ShouldAllBe(x => x.Source == AuditSource.Snapshot && x.Action == AuditAction.Delete);
        PiiPaths(rows[0]).ShouldBe(["Delivery"]);
    }

    [Fact]
    public async Task It_joins_the_callers_transaction_and_rolls_back_with_it()
    {
        var before = await _db.ReadAsync(db => db.Products.Where(x => x.Category == "Office").SumAsync(x => x.Price));

        await _db.AsAsync(Persona.Pricing, "Products.AdjustPrices", async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Products.Where(x => x.Category == "Office").ExecuteAuditedUpdateAsync(db, s => s.SetProperty(x => x.Price, 1m));
            await transaction.RollbackAsync();

            return 0;
        });

        (await _db.AuditAsync()).ShouldBeEmpty();
        (await _db.ReadAsync(db => db.Products.Where(x => x.Category == "Office").SumAsync(x => x.Price))).ShouldBe(before);
    }

    [Fact]
    public async Task It_does_not_flush_or_audit_the_callers_pending_changes()
    {
        await _db.AsAsync(Persona.Operations, "Mixed", async db =>
        {
            var supplier = await db.Suppliers.OrderBy(x => x.Id).FirstAsync();
            supplier.Name = "Renamed but never saved";

            await db.Products.Where(x => x.Category == "Garden")
                .ExecuteAuditedUpdateAsync(db, s => s.SetProperty(x => x.Stock, 20));

            db.Entry(supplier).State.ShouldBe(EntityState.Modified);

            return 0;
        });

        var rows = await _db.AuditAsync();
        rows.ShouldAllBe(x => x.EntityType == "Product");
        (await _db.ReadAsync(db => db.Suppliers.AnyAsync(x => x.Name == "Renamed but never saved"))).ShouldBeFalse();
    }

    [Fact]
    public async Task The_before_snapshot_locks_its_rows_and_the_statement_is_pinned_to_them()
    {
        _log.Clear();

        await _db.AsAsync(Persona.Pricing, "Products.AdjustPrices", db =>
            db.Products.Where(x => x.Category == "Kitchen").ExecuteAuditedUpdateAsync(db, s => s.SetProperty(x => x.Stock, 14)));

        var commands = _log.Commands;
        var select = commands.FindIndex(x => x.Contains("FOR UPDATE", StringComparison.Ordinal));
        var update = commands.FindIndex(x => x.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));

        select.ShouldBeGreaterThanOrEqualTo(0);
        update.ShouldBeGreaterThan(select);
        commands[update].ShouldNotContain("category", Case.Insensitive);
    }

    [Fact]
    public async Task Nothing_matching_means_nothing_written()
    {
        var affected = await _db.AsAsync(Persona.Pricing, "Products.AdjustPrices", db =>
            db.Products.Where(x => x.Category == "Atlantis").ExecuteAuditedUpdateAsync(db, s => s.SetProperty(x => x.Price, 1m)));

        affected.ShouldBe(0);
        (await _db.AuditAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cascaded_rows_of_an_audited_delete_are_still_caught_by_the_trigger()
    {
        await _db.AsAsync(Persona.Operations, "Products.Delete", db =>
            db.Products.Where(x => x.Sku == "KIT-01").ExecuteAuditedDeleteAsync(db));

        var rows = await _db.AuditAsync();
        rows.Single(x => x.EntityType == "Product").Source.ShouldBe(AuditSource.Snapshot);
        rows.Where(x => x.EntityType == "Order").ShouldNotBeEmpty();
        rows.Where(x => x.EntityType == "Order").ShouldAllBe(x => x.Source == AuditSource.Trigger && x.Operation == "Products.Delete");
    }

    /// <summary>Records the SQL EF sends, after the audit interceptor has rewritten it.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public List<string> Commands => [.. _commands];

        public void Clear() => _commands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
