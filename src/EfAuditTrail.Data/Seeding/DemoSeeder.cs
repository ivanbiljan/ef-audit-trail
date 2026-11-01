using EfAuditTrail.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfAuditTrail.Data.Seeding;

/// <summary>Reference data for the demo: a small shop. Inserts only — inserts are not audited, so a fresh
/// database has an empty audit trail until <see cref="ActivityGenerator"/> runs.</summary>
public static class DemoSeeder
{
    public static readonly string[] Categories = ["Outdoor", "Kitchen", "Garden", "Office"];

    public static async Task SeedAsync(EfAuditTrailDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Companies.AnyAsync(cancellationToken))
        {
            return;
        }

        var company = new Company { Name = "Demo Company" };
        db.Companies.Add(company);
        await db.SaveChangesAsync(cancellationToken);

        var suppliers = new[]
        {
            new Supplier { CompanyId = company.Id, Name = "Summit Outfitters", ContactName = "Robin Hale", Country = "Canada", CommissionRate = 12.50m },
            new Supplier { CompanyId = company.Id, Name = "Copper Pan Co.", ContactName = "Ines Duarte", Country = "Portugal", CommissionRate = 9.00m },
            new Supplier { CompanyId = company.Id, Name = "Greenleaf Tools", ContactName = "Mats Keller", Country = "Germany", CommissionRate = 10.00m },
            new Supplier { CompanyId = company.Id, Name = "Deskworks", ContactName = "Ana Kovač", Country = "Croatia", CommissionRate = 8.50m },
        };
        db.Suppliers.AddRange(suppliers);
        await db.SaveChangesAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var products = new[]
        {
            Product(company.Id, "OUT-01", "Trail Backpack 40L", "Outdoor", today.AddDays(-20), 129.00m, suppliers[0].Id, ProductStatus.Active),
            Product(company.Id, "OUT-02", "Ultralight Tent", "Outdoor", today.AddDays(45), 349.00m, suppliers[0].Id, ProductStatus.Active),
            Product(company.Id, "OUT-03", "Insulated Jacket", "Outdoor", today.AddDays(120), 219.00m, null, ProductStatus.Draft),
            Product(company.Id, "KIT-01", "Cast Iron Skillet", "Kitchen", today.AddDays(-5), 59.90m, suppliers[1].Id, ProductStatus.OutOfStock),
            Product(company.Id, "KIT-02", "Chef Knife Set", "Kitchen", today.AddDays(60), 149.00m, suppliers[1].Id, ProductStatus.Active),
            Product(company.Id, "GAR-01", "Cordless Hedge Trimmer", "Garden", today.AddDays(-40), 189.00m, suppliers[2].Id, ProductStatus.Active),
            Product(company.Id, "GAR-02", "Raised Garden Bed", "Garden", today.AddDays(30), 99.00m, suppliers[2].Id, ProductStatus.Active),
            Product(company.Id, "OFF-01", "Standing Desk", "Office", today.AddDays(15), 549.00m, suppliers[3].Id, ProductStatus.Active),
            Product(company.Id, "OFF-02", "Ergonomic Chair", "Office", today.AddDays(90), 399.00m, suppliers[3].Id, ProductStatus.Active),
        };
        db.Products.AddRange(products);

        string[] names =
        [
            "Avery Johnson", "Blake Carter", "Casey Nguyen", "Dana Fischer", "Elliot Rossi", "Frankie Silva",
            "Gray Tanaka", "Harper Novak", "Indigo Patel", "Jules Martin", "Kai Okafor", "Logan Berg",
        ];
        var customers = names.Select((name, i) => new Customer
        {
            CompanyId = company.Id,
            FullName = name,
            Email = $"{name.Split(' ')[0].ToLowerInvariant()}.{i + 1}@example.com",
            Phone = $"+1 555 01{i:00}",
            LoyaltyTier = (LoyaltyTier)(i % 3),
            MarketingOptIn = i % 2 == 0,
            Profile = $$"""
                {"language": "{{(i % 4 == 0 ? "de" : "en")}}", "interests": ["cooking"{{(i % 3 == 0 ? ", \"camping\"" : string.Empty)}}],
                 "newsletter": {{(i % 2 == 0 ? "true" : "false")}},
                 "deliveryContact": {"name": "Contact of {{name}}", "phone": "+1 555 02{{i:00}}"},
                 "identity": {"country": "US", "number": "ID{{1000000 + i}}"} }
                """,
        }).ToList();
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync(cancellationToken);

        var orders = new List<Order>();
        for (var i = 0; i < 20; i++)
        {
            var product = products[i % products.Length];
            var customer = customers[(i * 5) % customers.Count];
            var quantity = 1 + (i % 3);
            orders.Add(new Order
            {
                CompanyId = company.Id,
                ProductId = product.Id,
                CustomerId = customer.Id,
                Quantity = quantity,
                Status = (i % 7) switch { 0 => OrderStatus.Cancelled, 1 or 2 => OrderStatus.Pending, _ => OrderStatus.Confirmed },
                TotalPrice = product.Price * quantity,
                Delivery = Delivery(customer.FullName, quantity, i),
                InternalNotes = i % 4 == 0 ? "Customer asked for gift wrapping." : null,
            });
        }

        db.Orders.AddRange(orders);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>One parcel per unit, the first addressed to the customer.</summary>
    public static string Delivery(string recipient, int parcels, int salt)
    {
        var items = Enumerable.Range(0, parcels)
            .Select(n => $$"""{"recipient": "{{(n == 0 ? recipient : $"Recipient {salt}-{n}")}}", "phone": "+1 555 03{{salt:00}}{{n}}", "size": "{{(n % 2 == 0 ? "small" : "large")}}"}""");

        return $$"""{"parcels": [{{string.Join(", ", items)}}]}""";
    }

    private static Product Product(
        int companyId, string sku, string name, string category, DateOnly availableUntil, decimal price, int? supplierId, ProductStatus status)
        => new()
        {
            CompanyId = companyId,
            Sku = sku,
            Name = name,
            Category = category,
            AvailableUntil = availableUntil,
            Price = price,
            Stock = 25,
            SupplierId = supplierId,
            Status = status,
            Attributes = $$"""
                {"specs": [{"name": "Category", "value": "{{category}}"}, {"name": "Warranty", "value": "2 years"}, {"name": "Weight", "value": "1.5 kg"}],
                 "tags": ["new", "bestseller"]}
                """,
        };
}
