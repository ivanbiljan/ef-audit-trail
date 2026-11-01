// Aspire orchestration for the audit-trail demo:
//   • PostgreSQL 16 (+ pgweb to poke at audit.entries directly),
//   • the migrator — a one-shot that creates the schema, installs the audit trigger machinery, seeds
//     reference data and replays a burst of activity through all three capture paths, then exits,
//   • the API — Warp HTTP endpoints (vertical slices) that read the audit feed and run the playground,
//   • the web — a Blazor back office that renders the feed and drives the playground through the API.
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("16")
    .WithPgWeb();

var database = postgres.AddDatabase("auditdb");

var migrator = builder.AddProject<Projects.EfAuditTrail_Migrator>("migrator")
    .WithReference(database)
    .WaitFor(database);

var api = builder.AddProject<Projects.EfAuditTrail_Api>("api")
    .WithReference(database)
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrator);

builder.AddProject<Projects.EfAuditTrail_Web>("web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(api)
    .WaitFor(api);

await builder.Build().RunAsync();
