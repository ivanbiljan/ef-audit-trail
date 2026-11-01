using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EfAuditTrail.Data;
using EfAuditTrail.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Handlers;
using Warp.Http;

namespace EfAuditTrail.Api.Features.Audit;

/// <summary>The audit feed, newest first, keyset-paged on id. Filters combine with AND.
/// Personal data is masked unless <see cref="RevealPersonalData"/> is set — that is what <c>pii_paths</c> is for.</summary>
public sealed record GetAuditFeed(
    [FromQuery] string? EntityType = null,
    [FromQuery] string? EntityKey = null,
    [FromQuery] AuditSource? Source = null,
    [FromQuery] AuditAction? Action = null,
    [FromQuery] string? Actor = null,
    [FromQuery] string? Operation = null,
    [FromQuery] long? Before = null,
    [FromQuery] int? Take = null,
    [FromQuery] bool? RevealPersonalData = null) : IRequest<AuditFeedPage>;

public sealed record AuditFeedPage(IReadOnlyList<AuditEntryDto> Items, long? NextBefore);

public sealed record AuditEntryDto(
    long Id,
    DateTime OccurredAt,
    AuditSource Source,
    AuditAction Action,
    string EntityType,
    string EntityKey,
    int? CompanyId,
    string Operation,
    AuditActorKind ActorKind,
    int? ActorId,
    string? ActorDisplay,
    int? OnBehalfOfCustomerId,
    AuditChannel Channel,
    string? IpAddress,
    string? UserAgent,
    IReadOnlyList<AuditChangeDto> Changes);

/// <summary>One <c>changes</c> key. Values are raw JSON text (so a number stays a number); null when masked.</summary>
public sealed record AuditChangeDto(string Path, string? Old, string? New, bool PersonalData, bool Masked);

[WarpHttpGet("/api/audit")]
public sealed class GetAuditFeedHandler(EfAuditTrailDbContext db) : IRequestHandler<GetAuditFeed, AuditFeedPage>
{
    // Values are shown to people, so no \u002B for the "+" of a phone number. Rendered as text, never as HTML.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public async Task<AuditFeedPage> HandleAsync(GetAuditFeed request, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(request.Take ?? DefaultTake, 1, MaxTake);
        var query = db.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            query = query.Where(x => x.EntityType == request.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(request.EntityKey))
        {
            query = query.Where(x => x.EntityKey == request.EntityKey);
        }

        if (request.Source is { } source)
        {
            query = query.Where(x => x.Source == source);
        }

        if (request.Action is { } action)
        {
            query = query.Where(x => x.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(request.Actor))
        {
            var pattern = $"%{request.Actor.Trim()}%";
            query = query.Where(x => x.ActorDisplay != null && EF.Functions.ILike(x.ActorDisplay, pattern));
        }

        if (!string.IsNullOrWhiteSpace(request.Operation))
        {
            query = query.Where(x => x.Operation == request.Operation);
        }

        if (request.Before is { } before)
        {
            query = query.Where(x => x.Id < before);
        }

        var rows = await query.OrderByDescending(x => x.Id).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).Select(row => ToDto(row, request.RevealPersonalData == true)).ToList();

        return new AuditFeedPage(page, rows.Count > take ? page[^1].Id : null);
    }

    internal static AuditEntryDto ToDto(AuditEntry row, bool reveal)
    {
        var pii = JsonSerializer.Deserialize<string[]>(row.PiiPaths)?.ToHashSet(StringComparer.Ordinal) ?? [];
        var changes = new List<AuditChangeDto>();

        if (JsonNode.Parse(row.Changes) is JsonObject map)
        {
            foreach (var (path, value) in map.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var personal = pii.Contains(path);
                var masked = personal && !reveal;
                changes.Add(new AuditChangeDto(
                    path,
                    masked ? null : value?["old"]?.ToJsonString(Readable),
                    masked ? null : value?["new"]?.ToJsonString(Readable),
                    personal,
                    masked));
            }
        }

        return new AuditEntryDto(
            row.Id,
            DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc),
            row.Source,
            row.Action,
            row.EntityType,
            row.EntityKey,
            row.CompanyId,
            row.Operation,
            row.ActorKind,
            row.ActorId,
            row.ActorDisplay,
            row.OnBehalfOfCustomerId,
            row.Channel,
            row.IpAddress,
            row.UserAgent,
            changes);
    }
}
