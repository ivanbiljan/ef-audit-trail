using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Auditing.Session;
using EfAuditTrail.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfAuditTrail.Data;

/// <summary>Where the database lives — also used to open the out-of-band connection the playground uses to
/// write past the application, the way psql would.</summary>
public sealed record DemoDatabase(string ConnectionString);

public static class DataServiceCollectionExtensions
{
    /// <summary>Registers the context with the session interceptor and the scoped <see cref="AuditContext"/> it reads.
    /// The host fills the audit context in when its scope opens (middleware, job, seeder).</summary>
    public static IServiceCollection AddEfAuditTrailData(
        this IServiceCollection services,
        string connectionString,
        Action<DbContextOptionsBuilder>? configure = null)
        => services.AddEfAuditTrailData(_ => connectionString, configure);

    /// <summary>Resolves the connection string lazily — when the first context is created, after every
    /// configuration source (including a test host's overrides) has been applied.</summary>
    public static IServiceCollection AddEfAuditTrailData(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        Action<DbContextOptionsBuilder>? configure = null)
    {
        services.AddSingleton(sp => new DemoDatabase(connectionString(sp)));
        services.AddScoped<AuditContext>();
        services.AddScoped<AuditSessionInterceptor>();

        // Plain AddDbContext (Warp needs the scoped context, not only a factory) and no retrying execution
        // strategy (a user transaction would need wrapping on every call site).
        services.AddDbContext<EfAuditTrailDbContext>((sp, options) =>
        {
            options
                .UseNpgsql(sp.GetRequiredService<DemoDatabase>().ConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<AuditSessionInterceptor>());

            configure?.Invoke(options);
        });

        services.AddSingleton<ActivityGenerator>();

        return services;
    }
}
