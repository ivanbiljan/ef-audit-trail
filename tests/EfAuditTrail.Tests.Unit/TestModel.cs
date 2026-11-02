using EfAuditTrail.Data;
using Microsoft.EntityFrameworkCore;

namespace EfAuditTrail.Tests.Unit;

internal static class TestModel
{
    /// <summary>InMemory — enough to build the model the convention annotates.</summary>
    public static EfAuditTrailDbContext InMemory()
        => new(
            new DbContextOptionsBuilder<EfAuditTrailDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    /// <summary>Npgsql provider with the snake_case convention, never connected — gives the relational
    /// model (table/column names, store types) the trigger registration reads.</summary>
    public static EfAuditTrailDbContext Relational()
        => new(
            new DbContextOptionsBuilder<EfAuditTrailDbContext>()
                .UseNpgsql("Host=localhost;Database=model-only")
                .UseSnakeCaseNamingConvention()
                .Options);
}
