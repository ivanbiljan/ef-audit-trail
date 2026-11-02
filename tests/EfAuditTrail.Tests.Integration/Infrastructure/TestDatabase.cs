using System.Text.Json;
using System.Text.Json.Nodes;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Auditing.Triggers;
using EfAuditTrail.Data.Entities;
using EfAuditTrail.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EfAuditTrail.Tests.Integration.Infrastructure;

/// <summary>A fresh, seeded database with the trigger machinery installed and the real DI registration
/// (the session interceptor). Work runs in its own scope as a <see cref="Persona"/>, like a request would.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private TestDatabase(string name, string connectionString, ServiceProvider services)
    {
        Name = name;
        ConnectionString = connectionString;
        _services = services;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public IServiceProvider Services => _services;

    public static async Task<TestDatabase> CreateAsync(Action<DbContextOptionsBuilder>? configure = null, bool seed = true)
    {
        var name = $"audit_{Guid.NewGuid():N}";
        var connectionString = await SharedPostgres.CreateDatabaseAsync(name);
        var services = new ServiceCollection()
            .AddEfAuditTrailData(connectionString, configure)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var database = new TestDatabase(name, connectionString, services);
        await database.AsAsync(Persona.Seeder, "tests.setup", async db =>
        {
            await db.Database.EnsureCreatedAsync();
            await AuditTriggerInstaller.InstallAsync(db);
            if (seed)
            {
                await DemoSeeder.SeedAsync(db);
            }

            return 0;
        });

        return database;
    }

    public async Task<T> AsAsync<T>(Persona persona, string operation, Func<EfAuditTrailDbContext, Task<T>> work)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditContext>();
        persona.ApplyTo(context);

        using (context.BeginOperation(operation))
        {
            return await work(scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>());
        }
    }

    public async Task<T> ReadAsync<T>(Func<EfAuditTrailDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();

        return await read(scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>());
    }

    public Task<List<AuditEntry>> AuditAsync()
        => ReadAsync(db => db.AuditEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync());

    /// <summary>Straight to the database on a connection with no interceptors and no session context — psql.</summary>
    public async Task<int> ExecuteOutOfBandAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = "psql" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await SharedPostgres.DropDatabaseAsync(Name);
    }

    public static JsonObject Changes(AuditEntry row) => JsonNode.Parse(row.Changes)!.AsObject();

    public static string[] PiiPaths(AuditEntry row) => JsonSerializer.Deserialize<string[]>(row.PiiPaths)!;
}
