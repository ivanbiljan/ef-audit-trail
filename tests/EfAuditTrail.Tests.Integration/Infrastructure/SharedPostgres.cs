using Npgsql;
using Testcontainers.PostgreSql;

namespace EfAuditTrail.Tests.Integration.Infrastructure;

/// <summary>One PostgreSQL 16 container per test process; every test gets its own database in it, so tests
/// run in parallel without sharing rows. Ryuk removes the container when the process exits.
/// Set <c>AUDIT_TEST_POSTGRES</c> to an admin connection string to use an existing server instead (no Docker).</summary>
internal static class SharedPostgres
{
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static PostgreSqlContainer? _container;

    public static async Task<string> CreateDatabaseAsync(string name)
    {
        var admin = await AdminConnectionStringAsync();

        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"create database \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string name)
    {
        var admin = await AdminConnectionStringAsync();

        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"drop database if exists \"{name}\" with (force)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> AdminConnectionStringAsync()
    {
        if (Environment.GetEnvironmentVariable("AUDIT_TEST_POSTGRES") is { Length: > 0 } external)
        {
            return external;
        }

        if (_container is not null)
        {
            return _container.GetConnectionString();
        }

        await InitLock.WaitAsync();
        try
        {
            if (_container is null)
            {
                var container = new PostgreSqlBuilder("postgres:16-alpine")
                    .WithCommand("-c", "max_connections=300")
                    .Build();
                await container.StartAsync();
                _container = container;
            }

            return _container.GetConnectionString();
        }
        finally
        {
            InitLock.Release();
        }
    }
}
