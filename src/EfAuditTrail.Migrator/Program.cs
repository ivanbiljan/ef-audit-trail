using EfAuditTrail.Data;
using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Auditing.Triggers;
using EfAuditTrail.Data.Seeding;
using EfAuditTrail.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Provider.PostgreSql;

// One-shot schema provisioner. Aspire starts the API only after this exits 0 (WaitForCompletion).
//   1. schema: EnsureCreated — includes Warp's tables, which AddWarp adds to the model;
//   2. audit trigger machinery + one audit.enable() per audited entity, derived from the EF model;
//   3. reference data (inserts — not audited);
//   4. a burst of activity through every capture path, so the feed has something to show.
// Set AUDIT_DEMO_PRESERVE_DB=1 to keep an existing database (step 1 then only creates what is missing).
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("auditdb")
    ?? throw new InvalidOperationException("ConnectionStrings:auditdb is not configured.");

builder.Services.AddEfAuditTrailData(connectionString);
builder.Services.AddWarp<EfAuditTrailDbContext>(o => o.UsePostgreSql());

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

try
{
    await using (var scope = host.Services.CreateAsyncScope())
    {
        Persona.Seeder.ApplyTo(scope.ServiceProvider.GetRequiredService<AuditContext>());
        var db = scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>();

        if (Environment.GetEnvironmentVariable("AUDIT_DEMO_PRESERVE_DB") != "1")
        {
            await db.Database.EnsureDeletedAsync();
        }

        await db.Database.EnsureCreatedAsync();
        await AuditTriggerInstaller.InstallAsync(db);
        await DemoSeeder.SeedAsync(db);
    }

    var steps = await host.Services.GetRequiredService<ActivityGenerator>().RunAsync();
    foreach (var step in steps)
    {
        logger.LogInformation("[migrator] {Operation}: {Rows} row(s) written via {Source}", step.Operation, step.RowsAffected, step.ExpectedSource);
    }

    logger.LogInformation("[migrator] schema provisioned, audit triggers installed, demo data seeded");
}
catch (Exception ex)
{
    // Non-zero exit, so WaitForCompletion keeps the API from starting against a half-built schema.
    throw new InvalidOperationException("[migrator] provisioning failed", ex);
}
