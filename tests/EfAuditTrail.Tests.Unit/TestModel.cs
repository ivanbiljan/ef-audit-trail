using EfAuditTrail.Data;
using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfAuditTrail.Tests.Unit;

internal static class TestModel
{
    public static AuditContext Context(AuditChannel? channel = AuditChannel.BackOffice)
        => new()
        {
            ActorKind = AuditActorKind.Staff,
            ActorId = 7,
            ActorDisplay = "Alex Morgan",
            Channel = channel,
            IpAddress = "10.0.0.1",
            UserAgent = "tests",
        };

    /// <summary>InMemory — what the existing interceptor tests build on; enough for change tracking.</summary>
    public static EfAuditTrailDbContext InMemory(AuditContext? audit = null)
        => new(
            new DbContextOptionsBuilder<EfAuditTrailDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            audit ?? Context());

    /// <summary>Npgsql provider with the snake_case convention, never connected — gives the relational
    /// model (table/column names, store types) the trigger registration reads.</summary>
    public static EfAuditTrailDbContext Relational()
        => new(
            new DbContextOptionsBuilder<EfAuditTrailDbContext>()
                .UseNpgsql("Host=localhost;Database=model-only")
                .UseSnakeCaseNamingConvention()
                .Options,
            Context());

    public static Product Product(int id = 5) => new()
    {
        Id = id,
        CompanyId = 1,
        Sku = $"OUT-{id:00}",
        Name = "Trail Backpack 40L",
        Category = "Outdoor",
        AvailableUntil = new DateOnly(2026, 12, 1),
        Price = 4890.00m,
        Stock = 12,
        Status = ProductStatus.Active,
        Attributes = """{"days": [{"day": 1, "name": "Arrive"}], "gear": ["boots"]}""",
        ViewCount = 3,
    };

    public static Customer Customer(int id = 9) => new()
    {
        Id = id,
        CompanyId = 1,
        FullName = "Avery Johnson",
        Email = "avery@example.com",
        Phone = "+1 555 0100",
        LoyaltyTier = LoyaltyTier.Silver,
        Profile = """{"language": "en", "deliveryContact": {"name": "Morgan Ellis", "phone": "1"}, "identity": {"number": "P1"}}""",
    };
}
