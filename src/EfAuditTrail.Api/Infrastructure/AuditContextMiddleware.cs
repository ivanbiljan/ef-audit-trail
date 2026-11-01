using EfAuditTrail.Data.Auditing;
using EfAuditTrail.Data.Entities;

namespace EfAuditTrail.Api.Infrastructure;

/// <summary>
/// Opens the request's <see cref="AuditContext"/>: the "who" half of every audit row. In a real host this comes
/// from the authenticated principal; the demo has no login, so the caller names its persona in headers
/// (the Blazor back office sends the one picked in its UI). Never trust these headers outside a demo.
/// </summary>
public sealed class AuditContextMiddleware(RequestDelegate next)
{
    public const string ActorKindHeader = "X-Actor-Kind";
    public const string ActorIdHeader = "X-Actor-Id";
    public const string ActorDisplayHeader = "X-Actor-Display";
    public const string ChannelHeader = "X-Actor-Channel";
    public const string OnBehalfOfHeader = "X-On-Behalf-Of-Customer";
    public const string ClientIpHeader = "X-Client-Ip";
    public const string ClientUserAgentHeader = "X-Client-User-Agent";

    public Task InvokeAsync(HttpContext http, AuditContext audit)
    {
        var headers = http.Request.Headers;

        audit.Channel = Enum.TryParse<AuditChannel>(headers[ChannelHeader].ToString(), ignoreCase: true, out var channel)
            ? channel
            : AuditChannel.Api;
        audit.ActorKind = Enum.TryParse<AuditActorKind>(headers[ActorKindHeader].ToString(), ignoreCase: true, out var kind)
            ? kind
            : AuditActorKind.Anonymous;
        audit.ActorId = int.TryParse(headers[ActorIdHeader].ToString(), out var id) ? id : null;
        audit.ActorDisplay = NullIfEmpty(headers[ActorDisplayHeader].ToString());
        audit.OnBehalfOfCustomerId = int.TryParse(headers[OnBehalfOfHeader].ToString(), out var customer) ? customer : null;

        // A server-side caller (the Blazor circuit) forwards its user's address and browser; a direct caller is its own.
        audit.IpAddress = NullIfEmpty(headers[ClientIpHeader].ToString()) ?? http.Connection.RemoteIpAddress?.ToString();
        audit.UserAgent = NullIfEmpty(headers[ClientUserAgentHeader].ToString()) ?? NullIfEmpty(headers.UserAgent.ToString());

        return next(http);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
