using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EfAuditTrail.Api.Features.Audit;
using EfAuditTrail.Api.Features.Products;
using EfAuditTrail.Api.Infrastructure;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Auditing.Triggers;
using EfAuditTrail.Data.Entities;
using EfAuditTrail.Data.Seeding;
using EfAuditTrail.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfAuditTrail.Tests.Integration;

/// <summary>End to end through Warp's HTTP endpoints: the middleware supplies the "who", the Warp pipeline
/// behavior the "what", and the feed reads back what the trigger wrote.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private string _database = string.Empty;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _database = $"api_{Guid.NewGuid():N}";
        var connectionString = await SharedPostgres.CreateDatabaseAsync(_database);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:auditdb", connectionString));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            Persona.Seeder.ApplyTo(scope.ServiceProvider.GetRequiredService<AuditContext>());
            var db = scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>();
            await db.Database.EnsureCreatedAsync();
            await AuditTriggerInstaller.InstallAsync(db);
            await DemoSeeder.SeedAsync(db);
        }

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add(AuditContextMiddleware.ActorKindHeader, "Staff");
        _client.DefaultRequestHeaders.Add(AuditContextMiddleware.ActorIdHeader, "12");
        _client.DefaultRequestHeaders.Add(AuditContextMiddleware.ActorDisplayHeader, "Sam Lee (pricing)");
        _client.DefaultRequestHeaders.Add(AuditContextMiddleware.ChannelHeader, "BackOffice");
        _client.DefaultRequestHeaders.Add(AuditContextMiddleware.ClientIpHeader, "198.51.100.7");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await SharedPostgres.DropDatabaseAsync(_database);
    }

    [Fact]
    public async Task A_bulk_endpoint_is_audited_with_the_callers_identity_and_the_warp_operation()
    {
        var response = await _client.PostAsJsonAsync("/api/products/adjust-prices", new AdjustProductPrices("Garden", 10), Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<int>(Json)).ShouldBe(2);

        var page = await FeedAsync("/api/audit");

        page.Items.Count.ShouldBe(2);
        page.Items.ShouldAllBe(x =>
            x.Operation == "Products.AdjustProductPrices"
            && x.ActorDisplay == "Sam Lee (pricing)"
            && x.ActorId == 12
            && x.Channel == AuditChannel.BackOffice
            && x.IpAddress == "198.51.100.7");
        page.Items[0].Changes.Single().Path.ShouldBe("Price");
    }

    [Fact]
    public async Task Personal_data_is_masked_unless_revealed()
    {
        var customer = await FirstCustomerIdAsync();
        var response = await _client.PostAsJsonAsync("/api/customers/delivery-contact", new { customerId = customer, name = "Morgan Ellis", phone = "+1 555 0155", language = "fr" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var masked = (await FeedAsync("/api/audit?entityType=Customer")).Items.Single().Changes.ToDictionary(x => x.Path);
        masked["Profile/deliveryContact/phone"].ShouldSatisfyAllConditions(
            x => x.PersonalData.ShouldBeTrue(),
            x => x.Masked.ShouldBeTrue(),
            x => x.New.ShouldBeNull());
        masked["Profile/language"].Masked.ShouldBeFalse();
        masked["Profile/language"].New.ShouldBe("\"fr\"");

        var revealed = (await FeedAsync("/api/audit?entityType=Customer&revealPersonalData=true")).Items.Single().Changes.ToDictionary(x => x.Path);
        revealed["Profile/deliveryContact/phone"].New.ShouldBe("\"+1 555 0155\"");
    }

    [Fact]
    public async Task A_bulk_write_to_ignored_columns_leaves_no_trace()
    {
        var response = await _client.PostAsJsonAsync("/api/products/record-views", new { }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FeedAsync("/api/audit")).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_feed_pages_newest_first()
    {
        (await _client.PostAsJsonAsync("/api/playground/activity", new { }, Json)).EnsureSuccessStatusCode();

        var first = await FeedAsync("/api/audit?take=5");
        first.Items.Count.ShouldBe(5);
        first.NextBefore.ShouldNotBeNull();

        var oldestOnFirstPage = first.Items[^1].Id;
        var second = await FeedAsync($"/api/audit?take=5&before={first.NextBefore}");
        second.Items.ShouldNotBeEmpty();
        second.Items.ShouldAllBe(x => x.Id < oldestOnFirstPage);
        first.Items.Select(x => x.Id).ShouldBeInOrder(SortDirection.Descending);

        var facets = await _client.GetFromJsonAsync<AuditFacets>("/api/audit/facets", Json);
        facets!.Total.ShouldBeGreaterThan(5);
        facets.EntityTypes.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_invalid_command_is_a_400_and_writes_nothing()
    {
        var response = await _client.PostAsJsonAsync("/api/products/adjust-prices", new AdjustProductPrices("Garden", 0), Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FeedAsync("/api/audit")).Items.ShouldBeEmpty();
    }

    [Fact]
    public void Operation_codes_are_feature_qualified()
        => OperationCode.For(typeof(AdjustProductPrices)).ShouldBe("Products.AdjustProductPrices");

    private async Task<AuditFeedPage> FeedAsync(string url)
        => (await _client.GetFromJsonAsync<AuditFeedPage>(url, Json))!;

    private async Task<int> FirstCustomerIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<EfAuditTrailDbContext>().Customers.MinAsync(x => x.Id);
    }
}
