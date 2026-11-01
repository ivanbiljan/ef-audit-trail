using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace EfAuditTrail.Web.Services;

// Wire contracts — mirrors of the API's slice records. Enums travel as strings (JsonStringEnumConverter on the API).
public sealed record AuditFeedPage(IReadOnlyList<AuditEntryDto> Items, long? NextBefore);

public sealed record AuditEntryDto(
    long Id,
    DateTime OccurredAt,
    string Source,
    string Action,
    string EntityType,
    string EntityKey,
    int? CompanyId,
    string Operation,
    string ActorKind,
    int? ActorId,
    string? ActorDisplay,
    int? OnBehalfOfCustomerId,
    string Channel,
    string? IpAddress,
    string? UserAgent,
    IReadOnlyList<AuditChangeDto> Changes);

public sealed record AuditChangeDto(string Path, string? Old, string? New, bool PersonalData, bool Masked);

public sealed record AuditFacets(int Total, IReadOnlyList<FacetCount> EntityTypes, IReadOnlyList<FacetCount> Sources, IReadOnlyList<FacetCount> Operations);

public sealed record FacetCount(string Value, int Count);

public sealed record ProductSummary(int Id, string Sku, string Name, string Category, DateOnly AvailableUntil, decimal Price, string Status, int ViewCount);

public sealed record CustomerSummary(int Id, string FullName, string LoyaltyTier);

public sealed record BulkResult(int RowsAffected, string Mode);

public sealed record ActivityStep(string Operation, string ExpectedSource, int RowsAffected);

public sealed record AuditFeedQuery(
    string? EntityType = null,
    string? EntityKey = null,
    string? Source = null,
    string? Action = null,
    string? Actor = null,
    string? Operation = null,
    long? Before = null,
    int Take = 30,
    bool RevealPersonalData = false);

public sealed class ApiException(string message) : Exception(message);

/// <summary>Typed client for the API. Every call carries the current persona as headers, which the API's
/// AuditContextMiddleware turns into the audit context — the "who" of the rows a call produces.</summary>
public sealed class AuditTrailApi(HttpClient http, PersonaState persona)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AuditFeedPage> GetFeedAsync(AuditFeedQuery query, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["entityType"] = query.EntityType,
            ["entityKey"] = query.EntityKey,
            ["source"] = query.Source,
            ["action"] = query.Action,
            ["actor"] = query.Actor,
            ["operation"] = query.Operation,
            ["before"] = query.Before?.ToString(CultureInfo.InvariantCulture),
            ["take"] = query.Take.ToString(CultureInfo.InvariantCulture),
            ["revealPersonalData"] = query.RevealPersonalData ? "true" : null,
        };

        var url = QueryHelpers.AddQueryString(
            "/api/audit",
            parameters.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value));

        return GetAsync<AuditFeedPage>(url, cancellationToken);
    }

    public Task<AuditFacets> GetFacetsAsync(CancellationToken cancellationToken = default)
        => GetAsync<AuditFacets>("/api/audit/facets", cancellationToken);

    public Task<List<ProductSummary>> GetProductsAsync(CancellationToken cancellationToken = default)
        => GetAsync<List<ProductSummary>>("/api/products", cancellationToken);

    public Task<List<CustomerSummary>> GetCustomersAsync(CancellationToken cancellationToken = default)
        => GetAsync<List<CustomerSummary>>("/api/customers", cancellationToken);

    public Task<ProductSummary> RenameProductAsync(int id, string name)
        => PostAsync<ProductSummary>("/api/products/rename", new { id, name });

    public Task<BulkResult> AdjustPricesAsync(string category, decimal percent, string mode)
        => PostAsync<BulkResult>("/api/products/adjust-prices", new { category, percent, mode });

    public Task<BulkResult> ArchiveExpiredAsync(string mode)
        => PostAsync<BulkResult>("/api/products/archive-expired", new { mode });

    public Task<int> RecordViewsAsync()
        => PostAsync<int>("/api/products/record-views", new { });

    public Task<BulkResult> CancelPendingOrdersAsync(int? productId, string mode)
        => PostAsync<BulkResult>("/api/orders/cancel-pending", new { productId, mode });

    public Task<BulkResult> PurgeCancelledOrdersAsync(string mode)
        => PostAsync<BulkResult>("/api/orders/purge-cancelled", new { mode });

    public Task<int> UpdateDeliveryContactAsync(int customerId, string name, string phone, string? language)
        => PostAsync<int>("/api/customers/delivery-contact", new { customerId, name, phone, language });

    public Task<BulkResult> UpgradeLoyaltyTierAsync(string from, string to, string mode)
        => PostAsync<BulkResult>("/api/customers/upgrade-tier", new { from, to, mode });

    public Task<int> RunOutOfBandSqlAsync()
        => PostAsync<int>("/api/playground/out-of-band", new { });

    public Task<List<ActivityStep>> GenerateActivityAsync()
        => PostAsync<List<ActivityStep>>("/api/playground/activity", new { });

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Stamp(request);

        return await SendAsync<T>(request, cancellationToken);
    }

    // Commands without fields post "{}" — Warp binds an empty request without a body too, but a JSON body is the contract.
    private async Task<T> PostAsync<T>(string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        Stamp(request);

        return await SendAsync<T>(request, CancellationToken.None);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadProblemAsync(response, cancellationToken);
            throw new ApiException($"{(int)response.StatusCode} {response.ReasonPhrase}{(detail is null ? string.Empty : $" — {detail}")}");
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new ApiException("The API returned an empty body.");
    }

    private static async Task<string?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            return problem.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Stamp(HttpRequestMessage request)
    {
        var current = persona.Current;
        request.Headers.Add("X-Actor-Kind", current.Kind);
        request.Headers.Add("X-Actor-Channel", current.Channel);
        request.Headers.Add("X-Client-User-Agent", "EfAuditTrail.BackOffice (Blazor Server)");

        if (current.Id is { } id)
        {
            request.Headers.Add("X-Actor-Id", id.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(current.Display))
        {
            request.Headers.Add("X-Actor-Display", current.Display);
        }

        if (current.OnBehalfOfCustomerId is { } customer)
        {
            request.Headers.Add("X-On-Behalf-Of-Customer", customer.ToString(CultureInfo.InvariantCulture));
        }
    }
}
