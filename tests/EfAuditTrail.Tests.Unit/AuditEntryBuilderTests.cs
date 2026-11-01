using System.Text.Json;
using System.Text.Json.Nodes;
using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfAuditTrail.Tests.Unit;

/// <summary>The builder both the interceptor and the snapshot path rely on.</summary>
public sealed class AuditEntryBuilderTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_modified_property_becomes_one_row_with_the_scope_context()
    {
        var audit = TestModel.Context();
        using var db = TestModel.InMemory(audit);
        var product = TestModel.Product();
        db.Attach(product);
        product.Name = "Trail Backpack 40L Pro";

        using (audit.BeginOperation("Products.Rename"))
        {
            var row = AuditEntryBuilder.Build(db, audit, Now).ShouldHaveSingleItem();

            row.Action.ShouldBe(AuditAction.Update);
            row.EntityType.ShouldBe("Product");
            row.EntityKey.ShouldBe("5");
            row.CompanyId.ShouldBe(1);
            row.Operation.ShouldBe("Products.Rename");
            row.ActorDisplay.ShouldBe("Alex Morgan");
            row.Channel.ShouldBe(AuditChannel.BackOffice);
            row.Source.ShouldBe(AuditSource.Interceptor);
            row.OccurredAt.ShouldBe(Now);

            var title = Changes(row).ShouldHaveSingleItem();
            title.Key.ShouldBe("Name");
            title.Value!["old"]!.GetValue<string>().ShouldBe("Trail Backpack 40L");
            title.Value!["new"]!.GetValue<string>().ShouldBe("Trail Backpack 40L Pro");
            Pii(row).ShouldBeEmpty();
        }
    }

    [Fact]
    public void Changing_only_ignored_properties_produces_no_row()
    {
        using var db = TestModel.InMemory();
        var product = TestModel.Product();
        db.Attach(product);
        product.ViewCount++;
        product.UpdatedAt = Now;

        AuditEntryBuilder.Build(db, db.Audit, Now).ShouldBeEmpty();
    }

    [Fact]
    public void Enums_are_recorded_as_their_provider_value()
    {
        using var db = TestModel.InMemory();
        var product = TestModel.Product();
        db.Attach(product);
        product.Status = ProductStatus.Archived;

        var status = Changes(AuditEntryBuilder.Build(db, db.Audit, Now).Single())["Status"]!;

        status["old"]!.GetValue<string>().ShouldBe("Active");
        status["new"]!.GetValue<string>().ShouldBe("Archived");
    }

    [Fact]
    public void Jsonb_updates_are_diffed_to_leaves_and_only_personal_leaves_are_listed()
    {
        using var db = TestModel.InMemory();
        var customer = TestModel.Customer();
        db.Attach(customer);
        customer.Profile = """{"language": "fr", "deliveryContact": {"name": "Morgan Ellis", "phone": "2"}, "identity": {"number": "P1"}}""";

        var row = AuditEntryBuilder.Build(db, db.Audit, Now).Single();

        Changes(row).Select(x => x.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["Profile/deliveryContact/phone", "Profile/language"]);
        Pii(row).ShouldBe(["Profile/deliveryContact/phone"]);
    }

    [Fact]
    public void A_delete_snapshots_every_tracked_property()
    {
        using var db = TestModel.InMemory();
        var customer = TestModel.Customer();
        db.Attach(customer);
        db.Remove(customer);

        var row = AuditEntryBuilder.Build(db, db.Audit, Now).Single();
        var changes = Changes(row);

        row.Action.ShouldBe(AuditAction.Delete);
        changes.Select(x => x.Key).Order(StringComparer.Ordinal).ShouldBe(
            ["Email", "FullName", "LoyaltyTier", "MarketingOptIn", "Phone", "Profile"]);
        changes.ShouldAllBe(x => x.Value!["new"] == null);
        changes["Profile"]!["old"].ShouldBeOfType<JsonObject>();
        Pii(row).Order(StringComparer.Ordinal).ShouldBe(["Email", "FullName", "Phone", "Profile"]);
    }

    [Fact]
    public void Without_a_channel_the_save_fails_rather_than_writing_an_anonymous_row()
    {
        var audit = TestModel.Context(channel: null);
        using var db = TestModel.InMemory(audit);
        var product = TestModel.Product();
        db.Attach(product);
        product.Name = "x";

        Should.Throw<InvalidOperationException>(() => AuditEntryBuilder.Build(db, audit, Now))
            .Message.ShouldContain("AuditContext.Channel is not set");
    }

    [Fact]
    public void Without_an_operation_the_row_is_unattributed()
    {
        using var db = TestModel.InMemory();
        var product = TestModel.Product();
        db.Attach(product);
        product.Price = 1.00m;

        AuditEntryBuilder.Build(db, db.Audit, Now).Single().Operation.ShouldBe("unattributed");
    }

    [Fact]
    public void Inserts_are_not_audited()
    {
        using var db = TestModel.InMemory();
        db.Add(TestModel.Product());

        AuditEntryBuilder.Build(db, db.Audit, Now).ShouldBeEmpty();
    }

    private static JsonObject Changes(AuditEntry row) => JsonNode.Parse(row.Changes)!.AsObject();

    private static string[] Pii(AuditEntry row) => JsonSerializer.Deserialize<string[]>(row.PiiPaths)!;
}
