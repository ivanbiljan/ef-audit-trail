using EfAuditTrail.Data.Auditing.Session;
using EfAuditTrail.Data.Auditing.Snapshots;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfAuditTrail.Tests.Unit;

public sealed class KeyFilterTests
{
    [Fact]
    public void A_single_key_filter_matches_exactly_the_snapshot_rows()
    {
        using var db = TestModel.InMemory();
        var key = db.Model.FindEntityType(typeof(Product))!.FindPrimaryKey()!;

        var filter = KeyFilter.For(key, [TestModel.Product(2), TestModel.Product(5)]).Compile();

        filter(TestModel.Product(2)).ShouldBeTrue();
        filter(TestModel.Product(5)).ShouldBeTrue();
        filter(TestModel.Product(3)).ShouldBeFalse();
    }

    [Fact]
    public void A_composite_key_filter_requires_every_part()
    {
        using var db = new LegContext();
        var key = db.Model.FindEntityType(typeof(Leg))!.FindPrimaryKey()!;

        var filter = KeyFilter.For(key, [new Leg { TripId = 1, Index = 2 }, new Leg { TripId = 3, Index = 1 }]).Compile();

        filter(new Leg { TripId = 1, Index = 2 }).ShouldBeTrue();
        filter(new Leg { TripId = 3, Index = 1 }).ShouldBeTrue();
        filter(new Leg { TripId = 1, Index = 1 }).ShouldBeFalse();
        KeyFilter.ValueOf(key, new Leg { TripId = 1, Index = 2 }).ShouldBe("1|2");
    }

    [Fact]
    public void An_empty_snapshot_has_no_filter()
    {
        using var db = TestModel.InMemory();
        var key = db.Model.FindEntityType(typeof(Product))!.FindPrimaryKey()!;

        Should.Throw<ArgumentException>(() => KeyFilter.For<Product>(key, []));
    }

    public sealed class Leg
    {
        public int TripId { get; set; }

        public int Index { get; set; }
    }

    private sealed class LegContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseInMemoryDatabase(nameof(LegContext));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Leg>().HasKey(x => new { x.TripId, x.Index });
    }
}

public sealed class TriggerCaptureTests
{
    [Fact]
    public async Task Suppression_flows_across_awaits_nests_and_unwinds()
    {
        TriggerCapture.IsSuppressed.ShouldBeFalse();

        using (TriggerCapture.Suppress())
        {
            await Task.Yield();
            TriggerCapture.IsSuppressed.ShouldBeTrue();

            using (TriggerCapture.Suppress())
            {
                TriggerCapture.IsSuppressed.ShouldBeTrue();
            }

            TriggerCapture.IsSuppressed.ShouldBeTrue();
        }

        TriggerCapture.IsSuppressed.ShouldBeFalse();
    }

    [Fact]
    public async Task Suppression_does_not_leak_into_a_sibling_flow()
    {
        var sibling = Task.Run(async () =>
        {
            await Task.Delay(20);
            return TriggerCapture.IsSuppressed;
        });

        using (TriggerCapture.Suppress())
        {
            (await sibling).ShouldBeFalse();
        }
    }
}

public sealed class AuditSessionInterceptorTests
{
    [Theory]
    [InlineData(CommandSource.SaveChanges, false, true, true)]
    [InlineData(CommandSource.SaveChanges, true, true, true)]
    [InlineData(CommandSource.Unknown, false, true, false)]
    [InlineData(CommandSource.ExecuteSqlRaw, false, true, false)]
    [InlineData(CommandSource.Unknown, true, true, true)]
    [InlineData(CommandSource.LinqQuery, false, false, false)]
    [InlineData(CommandSource.LinqQuery, true, false, false)]
    [InlineData(CommandSource.Migrations, false, false, false)]
    public void Only_writes_publish_the_session_and_only_app_audited_writes_suppress_the_trigger(
        CommandSource source, bool suppressScope, bool publish, bool suppress)
        => AuditSessionInterceptor.Classify(source, suppressScope).ShouldBe((publish, suppress));
}
