using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfAuditTrail.Tests.Unit;

public sealed class AuditConventionTests
{
    private static readonly IModel Model = TestModel.InMemory().Model;

    private static string[]? Pii(string property)
        => Property<Customer>(property).FindRuntimeAnnotation(AuditAnnotations.PersonalData)?.Value as string[];

    [Fact]
    public void Business_entities_are_audited_and_the_audit_table_is_not()
    {
        AuditAnnotations.IsAudited(Entity<Product>()).ShouldBeTrue();
        AuditAnnotations.IsAudited(Entity<Customer>()).ShouldBeTrue();
        AuditAnnotations.IsAudited(Entity<AuditEntry>()).ShouldBeFalse();
    }

    [Theory]
    [InlineData(nameof(Product.Id))]
    [InlineData(nameof(Product.CompanyId))]
    [InlineData(nameof(Product.CreatedAt))]
    [InlineData(nameof(Product.UpdatedAt))]
    [InlineData(nameof(Product.ViewCount))]
    public void Keys_tenant_timestamps_and_AuditIgnore_properties_are_ignored(string property)
        => AuditAnnotations.IsIgnored(Property<Product>(property)).ShouldBeTrue();

    [Theory]
    [InlineData(nameof(Product.Name))]
    [InlineData(nameof(Product.Price))]
    [InlineData(nameof(Product.Attributes))]
    [InlineData(nameof(Product.LastReviewedAt))]
    public void Business_properties_are_tracked(string property)
        => AuditAnnotations.IsIgnored(Property<Product>(property)).ShouldBeFalse();

    [Fact]
    public void Jsonb_columns_are_flagged_for_leaf_diffs()
    {
        AuditAnnotations.IsJson(Property<Product>(nameof(Product.Attributes))).ShouldBeTrue();
        AuditAnnotations.IsJson(Property<Product>(nameof(Product.Name))).ShouldBeFalse();
    }

    [Fact]
    public void A_bare_AuditPii_marks_the_whole_value()
    {
        Pii(nameof(Customer.Email)).ShouldBeEmpty();
        Pii(nameof(Customer.LoyaltyTier)).ShouldBeNull();
    }

    [Fact]
    public void AuditPii_paths_are_carried_as_patterns()
        => Pii(nameof(Customer.Profile)).ShouldBe(["/deliveryContact/**", "/identity/**"], ignoreOrder: true);

    [Fact]
    public void Pii_paths_on_a_non_json_property_fail_the_model_build()
    {
        using var db = new MisconfiguredContext();

        var exception = Should.Throw<InvalidOperationException>(() => db.Model);

        exception.Message.ShouldContain("[AuditPii] paths apply only to a jsonb property");
    }

    [Fact]
    public void AuditIgnore_on_a_class_opts_the_entity_out()
    {
        using var db = new OptOutContext();

        AuditAnnotations.IsAudited(db.Model.FindEntityType(typeof(PageView))!).ShouldBeFalse();
    }

    private static IEntityType Entity<T>() => Model.FindEntityType(typeof(T))!;

    private static IProperty Property<T>(string name) => Entity<T>().FindProperty(name)!;

    public sealed class Misconfigured
    {
        public int Id { get; set; }

        [AuditPii("/first")]
        public string Name { get; set; } = string.Empty;
    }

    [AuditIgnore]
    public sealed class PageView
    {
        public int Id { get; set; }

        public string Url { get; set; } = string.Empty;
    }

    private sealed class MisconfiguredContext : DbContext
    {
        public DbSet<Misconfigured> Items => Set<Misconfigured>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseInMemoryDatabase(nameof(MisconfiguredContext));

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.Conventions.Add(_ => new AuditConvention());
    }

    private sealed class OptOutContext : DbContext
    {
        public DbSet<PageView> Views => Set<PageView>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseInMemoryDatabase(nameof(OptOutContext));

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.Conventions.Add(_ => new AuditConvention());
    }
}
