using System.Text.Json;

namespace EfAuditTrail.Tests.EndToEnd;

/// <summary>The whole Aspire graph: PostgreSQL 16, the migrator (schema, triggers, seed, activity), the API and
/// the Blazor web app. Slow (container + four processes) — run it before a release, not on every save.</summary>
[Trait("Category", "EndToEnd")]
public sealed class AppHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task The_migrator_seeds_activity_and_the_back_office_renders_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.EfAuditTrail_AppHost>(cancellationToken);
        await using var app = await builder.BuildAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);

        await app.ResourceNotifications.WaitForResourceAsync("migrator", KnownResourceStates.Finished, cancellationToken).WaitAsync(Timeout, cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken).WaitAsync(Timeout, cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("web", cancellationToken).WaitAsync(Timeout, cancellationToken);

        using var api = app.CreateHttpClient("api");
        using var facets = JsonDocument.Parse(await api.GetStringAsync("/api/audit/facets", cancellationToken));
        facets.RootElement.GetProperty("total").GetInt32().ShouldBeGreaterThan(0);

        using var web = app.CreateHttpClient("web");
        var html = await web.GetStringAsync("/", cancellationToken);
        html.ShouldContain("Audit trail");
    }
}
