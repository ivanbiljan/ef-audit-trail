using System.Text.Json.Serialization;
using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using EfAuditTrail.ServiceDefaults;
using Warp.Core;
using Warp.Http;
using Warp.Provider.PostgreSql;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddEfAuditTrailData(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("auditdb")
    ?? throw new InvalidOperationException("ConnectionStrings:auditdb is not configured."));

// Publisher-only Warp: the in-memory mediator + HTTP exposure of the feature handlers. The source generator
// registers every IRequestHandler / IPipelineBehavior in this assembly (incl. AuditOperationBehavior).
builder.Services.AddWarp<EfAuditTrailDbContext>(o => o.UsePostgreSql());
builder.Services.AddWarpHttp();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<AuditContextMiddleware>();

app.MapWarpHttp();
app.MapDefaultEndpoints();

await app.RunAsync();

public partial class Program;
