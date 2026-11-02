using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EfAuditTrail.Data.Configurations;

// Table and column names come from the snake_case naming convention (UseSnakeCaseNamingConvention) — the
// trigger registration reads them back off the model, so nothing here is duplicated in SQL.

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        // audit.entries is also written by audit.capture(); its column names are part of that contract
        // (Sql/audit.sql), so they are pinned here rather than left to the naming convention.
        builder.ToTable("entries", "audit");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        builder.Property(x => x.CompanyId).HasColumnName("company_id");
        builder.Property(x => x.Operation).HasColumnName("operation").HasMaxLength(200);
        builder.Property(x => x.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(100);
        builder.Property(x => x.EntityKey).HasColumnName("entity_key").HasMaxLength(200);
        builder.Property(x => x.ActorKind).HasColumnName("actor_kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ActorId).HasColumnName("actor_id");
        builder.Property(x => x.ActorDisplay).HasColumnName("actor_display").HasMaxLength(320);
        builder.Property(x => x.OnBehalfOfCustomerId).HasColumnName("on_behalf_of_customer_id");
        builder.Property(x => x.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(x => x.Changes).HasColumnName("changes").HasColumnType("jsonb");
        builder.Property(x => x.PiiPaths).HasColumnName("pii_paths").HasColumnType("jsonb");

        // The feed is keyset-paged on id desc; the entity history and per-tenant views filter first.
        builder.HasIndex(x => new { x.EntityType, x.EntityKey, x.Id }).IsDescending(false, false, true);
        builder.HasIndex(x => new { x.CompanyId, x.Id }).IsDescending(false, true);
        builder.HasIndex(x => x.OccurredAt);
    }
}

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(200);
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.Property(x => x.FullName).HasMaxLength(200);
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.Phone).HasMaxLength(40);
        builder.Property(x => x.LoyaltyTier).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Profile).HasColumnType("jsonb");
        builder.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        builder.HasIndex(x => x.Email).IsUnique();
    }
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.ContactName).HasMaxLength(200);
        builder.Property(x => x.Country).HasMaxLength(100);
        builder.Property(x => x.CommissionRate).HasPrecision(5, 2);
        builder.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(x => x.Sku).HasMaxLength(20);
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Category).HasMaxLength(50);
        builder.Property(x => x.Price).HasPrecision(10, 2);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Attributes).HasColumnType("jsonb");
        builder.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.Sku).IsUnique();
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.TotalPrice).HasPrecision(10, 2);
        builder.Property(x => x.Delivery).HasColumnType("jsonb");
        builder.Property(x => x.InternalNotes).HasMaxLength(2000);
        builder.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}
