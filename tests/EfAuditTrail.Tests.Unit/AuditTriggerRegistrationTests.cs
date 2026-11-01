using System.Text.Json.Nodes;
using EfAuditTrail.Data.Auditing.Triggers;

namespace EfAuditTrail.Tests.Unit;

/// <summary>The trigger is configured from the EF model, so these pin what each audit.enable() call receives.</summary>
public sealed class AuditTriggerRegistrationTests
{
    private static readonly IReadOnlyList<AuditTableRegistration> Registrations =
        AuditTriggerInstaller.BuildRegistrations(TestModel.Relational().Model);

    [Fact]
    public void Every_audited_entity_is_registered_and_the_audit_table_is_not()
        => Registrations.Select(x => x.EntityType).ShouldBe(["Company", "Customer", "Order", "Product", "Supplier"]);

    [Fact]
    public void Product_registers_its_table_key_tenant_and_tracked_columns()
    {
        var product = Registrations.Single(x => x.EntityType == "Product");

        product.Relation.ShouldBe("\"public\".\"products\"");
        product.KeyColumns.ShouldBe(["id"]);
        product.CompanyColumn.ShouldBe("company_id");

        var columns = product.Columns.ToDictionary(x => x.Column);
        columns.Keys.ShouldNotContain("id");
        columns.Keys.ShouldNotContain("company_id");
        columns.Keys.ShouldNotContain("created_at");
        columns.Keys.ShouldNotContain("updated_at");
        columns.Keys.ShouldNotContain("view_count");

        columns["name"].ShouldBe(new AuditColumn("name", "Name", "value", null));
        columns["attributes"].Kind.ShouldBe("json");
        columns["last_reviewed_at"].Kind.ShouldBe("timestamptz");
        columns["available_until"].Kind.ShouldBe("value");
        columns["status"].Property.ShouldBe("Status");
    }

    [Fact]
    public void Pii_patterns_travel_with_the_column()
    {
        var customer = Registrations.Single(x => x.EntityType == "Customer").Columns.ToDictionary(x => x.Column);

        customer["email"].Pii.ShouldNotBeNull().ShouldBeEmpty();
        customer["profile"].Pii.ShouldBe(["/deliveryContact/**", "/identity/**"]);
        customer["loyalty_tier"].Pii.ShouldBeNull();
    }

    [Fact]
    public void Entities_without_a_tenant_have_no_company_column()
        => Registrations.Single(x => x.EntityType == "Company").CompanyColumn.ShouldBeNull();

    [Fact]
    public void Columns_serialize_to_the_shape_audit_enable_expects()
    {
        var json = JsonNode.Parse(Registrations.Single(x => x.EntityType == "Customer").ColumnsJson)!.AsArray();
        var email = json.Single(x => x!["column"]!.GetValue<string>() == "email")!;

        email["property"]!.GetValue<string>().ShouldBe("Email");
        email["kind"]!.GetValue<string>().ShouldBe("value");
        email["pii"].ShouldBeOfType<JsonArray>().ShouldBeEmpty();
        json.Single(x => x!["column"]!.GetValue<string>() == "loyalty_tier")!["pii"].ShouldBeNull();
    }

    [Fact]
    public void The_install_script_is_embedded()
    {
        AuditTriggerInstaller.InstallScript.ShouldContain("create or replace function audit.capture()");
        AuditTriggerInstaller.InstallScript.ShouldContain("create or replace function audit.enable(");
    }
}
