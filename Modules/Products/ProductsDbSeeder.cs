// File: Modules/Products/ProductsDbSeeder.cs
using System.Threading.Tasks;
using System.Linq;
using Microsoft.EntityFrameworkCore;   // <-- needed for AnyAsync/FirstOrDefaultAsync


namespace Ultivision.Modules.Products
{
    public static class ProductsDbSeeder
    {
        public static async Task SeedAsync(AppDb db)
        {
            if (await db.Categories.AnyAsync()) return;

            // seed categories
            db.Categories.AddRange(
                new Category { Name = "Beverages", Description = "All kinds of drinks: tea, coffee, juices." },
                new Category { Name = "Snacks", Description = "Light bites, crisps, and confectionery." },
                new Category { Name = "Household", Description = "Cleaning materials and household supplies." }
            );
            await db.SaveChangesAsync();

            // seed products
            db.Products.AddRange(
                new Product { Name = "Green Tea - 250g", Description = "Premium matcha green tea leaves.", CategoryId = db.Categories.First().CategoryId, BasePrice = 3.50m, Stock = 100 },
                new Product { Name = "Instant Coffee 200g", Description = "Rich instant coffee blend.", CategoryId = db.Categories.Skip(0).First().CategoryId, BasePrice = 5.99m, Stock = 120 }
            );
            await db.SaveChangesAsync();

            // create suppliers for first product(s)
            var p1 = await db.Products.FirstOrDefaultAsync();
            if (p1 != null)
            {
                db.Suppliers.Add(new Supplier { ProductId = p1.ProductId, Name = "Supplier A", Price = 3.00m, ContactInfo = "contactA@example.com" });
                db.Suppliers.Add(new Supplier { ProductId = p1.ProductId, Name = "Supplier B", Price = 2.90m, ContactInfo = "contactB@example.com" });
            }

            await db.SaveChangesAsync();
        }
    }
}
