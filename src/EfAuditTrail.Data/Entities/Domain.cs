using EfAuditTrail.Data.Auditing;

namespace EfAuditTrail.Data.Entities;

/// <summary>The tenant. Not company-scoped itself, so its audit rows carry no company id.</summary>
public sealed class Company : IHaveTimestamps
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public enum LoyaltyTier
{
    Bronze,
    Silver,
    Gold,
    Platinum,
}

public sealed class Customer : ICompanyScoped, IHaveTimestamps
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    [AuditPii]
    public required string FullName { get; set; }

    [AuditPii]
    public required string Email { get; set; }

    [AuditPii]
    public string? Phone { get; set; }

    public LoyaltyTier LoyaltyTier { get; set; }

    public bool MarketingOptIn { get; set; }

    /// <summary>jsonb: language, interests, newsletter flag, delivery contact and identity document. Only the
    /// last two are personal data, so a language change is auditable in the clear.</summary>
    [AuditPii("/deliveryContact/**", "/identity/**")]
    public string Profile { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class Supplier : ICompanyScoped, IHaveTimestamps
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public required string Name { get; set; }

    /// <summary>A named person at the supplier — personal data, unlike the company name.</summary>
    [AuditPii]
    public required string ContactName { get; set; }

    public required string Country { get; set; }

    public decimal CommissionRate { get; set; }

    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public enum ProductStatus
{
    Draft,
    Active,
    OutOfStock,
    Archived,
}

public sealed class Product : ICompanyScoped, IHaveTimestamps
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public required string Sku { get; set; }

    public required string Name { get; set; }

    public required string Category { get; set; }

    /// <summary>Last day the product is sold; past it, maintenance archives the product.</summary>
    public DateOnly AvailableUntil { get; set; }

    public decimal Price { get; set; }

    public int Stock { get; set; }

    public ProductStatus Status { get; set; }

    public int? SupplierId { get; set; }

    /// <summary>jsonb: <c>{ "specs": [ { "name": "Weight", "value": "1.2 kg" } ], "tags": [ "…" ] }</c>.</summary>
    public string Attributes { get; set; } = "{}";

    /// <summary>Hot counter bumped on every page view — noise, not business state.</summary>
    [AuditIgnore]
    public int ViewCount { get; set; }

    /// <summary>A timestamptz the audit trail does track: proves the trigger formats it like System.Text.Json.</summary>
    public DateTime? LastReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public enum OrderStatus
{
    Pending,
    Confirmed,
    Cancelled,
}

public sealed class Order : ICompanyScoped, IHaveTimestamps
{
    public int Id { get; set; }

    public int CompanyId { get; set; }

    public int ProductId { get; set; }

    public int CustomerId { get; set; }

    public int Quantity { get; set; }

    public OrderStatus Status { get; set; }

    public decimal TotalPrice { get; set; }

    /// <summary>jsonb: <c>{ "parcels": [ { "recipient": "…", "phone": "…", "size": "…" } ] }</c>.</summary>
    [AuditPii("/parcels/*/recipient", "/parcels/*/phone")]
    public string Delivery { get; set; } = "{}";

    [AuditIgnore]
    public string? InternalNotes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
