namespace EfAuditTrail.Web.Services;

/// <summary>Who the back-office user is acting as. The demo has no login; a real back office would take
/// this from the authenticated principal and never let the user pick.</summary>
public sealed record Persona(string Key, string Label, string Kind, int? Id, string Display, string Channel, int? OnBehalfOfCustomerId = null)
{
    public static readonly IReadOnlyList<Persona> All =
    [
        new("operations", "Alex Morgan — operations", "Staff", 7, "Alex Morgan (ops)", "BackOffice"),
        new("pricing", "Sam Lee — pricing", "Staff", 12, "Sam Lee (pricing)", "BackOffice"),
        new("support", "Jordan Kim — support, for customer #2", "Staff", 21, "Jordan Kim (support)", "BackOffice", 2),
        new("shopper", "Avery Johnson — customer, mobile app", "Customer", 1, "Avery Johnson", "Api"),
        new("anonymous", "Anonymous caller", "Anonymous", null, "", "Api"),
    ];
}

public sealed class PersonaState
{
    public Persona Current { get; private set; } = Persona.All[0];

    public event Action? Changed;

    public void Select(string key)
    {
        Current = Persona.All.FirstOrDefault(x => x.Key == key) ?? Persona.All[0];
        Changed?.Invoke();
    }
}
