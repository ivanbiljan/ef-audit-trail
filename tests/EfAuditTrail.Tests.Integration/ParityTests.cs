using EfAuditTrail.Data.Auditing.Snapshots;
using EfAuditTrail.Data.Entities;
using EfAuditTrail.Data.Seeding;
using EfAuditTrail.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EfAuditTrail.Tests.Integration;

/// <summary>
/// The point of both approaches: a reader must not be able to tell which path wrote a row. Three rows are made
/// identical, the same change is applied through the interceptor, the trigger and the snapshot path, and the
/// resulting <c>changes</c> / <c>pii_paths</c> are compared as jsonb in the database.
/// </summary>
public sealed class ParityTests : IAsyncLifetime
{
    private const string Profile = """{"language": "fr", "interests": ["cooking", "camping"], "newsletter": false, "deliveryContact": {"name": "Morgan Ellis", "phone": "+1 555 0111"}, "identity": {"number": "N1"}}""";
    private TestDatabase _db = null!;
    private int[] _ids = [];

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _ids = (await _db.ReadAsync(db => db.Customers.OrderBy(x => x.Id).Select(x => x.Id).Take(3).ToListAsync())).ToArray();

        // Make the three customers identical apart from key and (unique) email, then forget that setup.
        await _db.ExecuteOutOfBandAsync($$"""
            update customers
               set full_name = 'Twin', phone = '+1 555 0000', loyalty_tier = 'Bronze', marketing_opt_in = false,
                   profile = '{"language": "en", "interests": ["cooking"], "newsletter": true, "deliveryContact": {"name": "Riley Quinn", "phone": "+1 555 0999"}, "identity": {"number": "N1"} }'
             where id in ({{string.Join(", ", _ids)}})
            """);
        await _db.ExecuteOutOfBandAsync("delete from audit.entries");
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Interceptor_trigger_and_snapshot_write_the_same_changes_for_the_same_update()
    {
        var (viaInterceptor, viaTrigger, viaSnapshot) = (_ids[0], _ids[1], _ids[2]);

        await _db.AsAsync(Persona.Pricing, "Parity", async db =>
        {
            var customer = await db.Customers.SingleAsync(x => x.Id == viaInterceptor);
            customer.LoyaltyTier = LoyaltyTier.Platinum;
            customer.Phone = "+1 555 0123";
            customer.MarketingOptIn = true;
            customer.Profile = Profile;

            return await db.SaveChangesAsync();
        });

        await _db.AsAsync(Persona.Pricing, "Parity", db =>
            db.Customers.Where(x => x.Id == viaTrigger).ExecuteUpdateAsync(Setters));

        await _db.AsAsync(Persona.Pricing, "Parity", db =>
            db.Customers.Where(x => x.Id == viaSnapshot).ExecuteAuditedUpdateAsync(db, Setters));

        var rows = await _db.AuditAsync();
        rows.Select(x => x.Source).ShouldBe([AuditSource.Interceptor, AuditSource.Trigger, AuditSource.Snapshot]);
        rows.Select(x => (x.Action, x.EntityType, x.Operation, x.ActorDisplay, x.Channel, x.CompanyId)).Distinct().ShouldHaveSingleItem();

        (await SameAsync(rows[0].Id, rows[1].Id)).ShouldBeTrue("interceptor vs trigger");
        (await SameAsync(rows[1].Id, rows[2].Id)).ShouldBeTrue("trigger vs snapshot");
    }

    [Fact]
    public async Task Trigger_and_snapshot_write_the_same_changes_for_the_same_delete()
    {
        await _db.AsAsync(Persona.NightlyJob, "Parity", db => db.Customers.Where(x => x.Id == _ids[1]).ExecuteDeleteAsync());
        await _db.AsAsync(Persona.NightlyJob, "Parity", db => db.Customers.Where(x => x.Id == _ids[2]).ExecuteAuditedDeleteAsync(db));

        var rows = (await _db.AuditAsync()).Where(x => x.EntityType == "Customer").ToList();
        rows.Select(x => x.Source).ShouldBe([AuditSource.Trigger, AuditSource.Snapshot]);

        // Emails stay unique, so compare everything but Email.
        (await SameAsync(rows[0].Id, rows[1].Id, except: "Email")).ShouldBeTrue();
    }

    private static void Setters(Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Customer> s)
    {
        s.SetProperty(x => x.LoyaltyTier, LoyaltyTier.Platinum);
        s.SetProperty(x => x.Phone, "+1 555 0123");
        s.SetProperty(x => x.MarketingOptIn, true);
        s.SetProperty(x => x.Profile, Profile);
    }

    // jsonb equality: key order and number formatting do not matter; pii_paths compared as sets.
    private Task<bool> SameAsync(long a, long b, string? except = null)
        => _db.ScalarAsync<bool>($"""
            select (x.changes - '{except ?? string.Empty}') = (y.changes - '{except ?? string.Empty}')
               and (select coalesce(jsonb_agg(v order by v), '[]') from jsonb_array_elements_text(x.pii_paths) v)
                 = (select coalesce(jsonb_agg(v order by v), '[]') from jsonb_array_elements_text(y.pii_paths) v)
              from audit.entries x, audit.entries y
             where x.id = {a} and y.id = {b}
            """);
}
