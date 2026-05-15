using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Security.Claims;
using Ultivision.Modules.Products;
using Ultivision.Modules.Products.Dtos;


namespace Ultivision.Modules.Products
{
    public static class ProductsEndpoints
    {
        public static void MapProductsEndpoints(this WebApplication app)
        {
            // ============================
            // PUBLIC ENDPOINTS (NO AUTH)
            // ============================

            // Categories
            app.MapGet("/api/products/categories", async (AppDb db) =>
            {
                var cats = await db.Categories
                    .AsNoTracking()
                    .OrderBy(c => c.Name)
                    .ToListAsync();

                return Results.Ok(cats.Select(c => new
                {
                    c.CategoryId,
                    c.Name,
                    c.Description,
                    c.ImageUrl,
                    c.IsActive
                }));
            }).WithTags("Products");

            // Products list (paging, filter)
            app.MapGet("/api/products", async (
                AppDb db,
                int? categoryId,
                string? search,
                string? sort,
                int page = 1,
                int pageSize = 50) =>
            {
                var q =
                    from prod in db.Products.AsNoTracking()
                    join cat in db.Categories.AsNoTracking()
                        on prod.CategoryId equals cat.CategoryId into gj
                    from cat in gj.DefaultIfEmpty()
                    select new
                    {
                        prod.ProductId,
                        prod.Name,
                        prod.Description,
                        prod.CategoryId,
                        CategoryName = cat != null ? cat.Name : null,
                        prod.ImageUrl,
                        prod.BasePrice,
                        prod.Stock,
                        prod.IsActive,
                        prod.CreatedAt
                    };

                if (categoryId.HasValue)
                    q = q.Where(p => p.CategoryId == categoryId.Value);

                if (!string.IsNullOrWhiteSpace(search))
                    q = q.Where(p =>
                        (p.Name ?? "").Contains(search) ||
                        (p.Description ?? "").Contains(search));

                q = sort switch
                {
                    "price_asc" => q.OrderBy(p => p.BasePrice),
                    "price_desc" => q.OrderByDescending(p => p.BasePrice),
                    _ => q.OrderByDescending(p => p.CreatedAt)
                };

                var total = await q.CountAsync();
                var items = await q
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return Results.Ok(new { total, page, pageSize, items });
            }).WithTags("Products");

            // Product details
            app.MapGet("/api/products/{productId:int}", async (AppDb db, int productId) =>
            {
                var product = await db.Products
                    .AsNoTracking()
                    .Where(p => p.ProductId == productId)
                    .Select(p => new
                    {
                        p.ProductId,
                        p.Name,
                        p.Description,
                        p.CategoryId,
                        CategoryName = db.Categories
                            .Where(c => c.CategoryId == p.CategoryId)
                            .Select(c => c.Name)
                            .FirstOrDefault(),
                        p.ImageUrl,
                        p.BasePrice,
                        p.Stock,
                        p.IsActive,
                        p.CreatedAt
                    })
                    .FirstOrDefaultAsync();

                if (product == null)
                    return Results.NotFound();

                var suppliers = await db.Suppliers
                    .AsNoTracking()
                    .Where(s => s.ProductId == productId && s.IsActive)
                    .Select(s => new
                    {
                        s.SupplierId,
                        s.Name,
                        s.Price,
                        s.ContactInfo
                    })
                    .ToListAsync();

                return Results.Ok(new { product, suppliers });
            }).WithTags("Products");

            // Supplier inventory
            app.MapGet("/api/products/{productId:int}/supplierinventory",
                async (int productId, AppDb db) =>
            {
                var items = await db.SupplierInventory
                    .AsNoTracking()
                    .Where(si => si.ProductId == productId && si.IsActive)
                    .OrderBy(si => si.SupplierPrice)
                    .Select(si => new
                    {
                        si.SupplierInventoryId,
                        si.SupplierId,
                        si.ProductId,
                        si.QuantityAvailable,
                        si.SupplierPrice,
                        si.LeadTimeDays,
                        SupplierName = db.Suppliers
                            .Where(s => s.SupplierId == si.SupplierId)
                            .Select(s => s.Name)
                            .FirstOrDefault()
                    })
                    .ToListAsync();

                return Results.Ok(items);
            }).WithTags("Products");


            // ============================
            // PROTECTED ENDPOINTS (JWT)
            // ============================

            // Add to cart
 app.MapPost("/api/cart", async (
    HttpContext ctx,
    AppDb db,
    AddToCartDto dto) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null)
        return Results.Unauthorized();

    var item = new CartItem
    {
        UserId = userId,
        ProductId = dto.ProductId,
        ProductName = dto.ProductName,
        SupplierId = dto.SupplierId,
        SupplierName = dto.SupplierName,
        Quantity = dto.Quantity,
        UnitPrice = dto.UnitPrice,
        CreatedAt = DateTime.UtcNow

    };

    db.CartItems.Add(item);
    await db.SaveChangesAsync();

    return Results.Created($"/api/cart/{item.CartItemId}", item);
})
.RequireAuthorization()
.WithTags("Cart");


            // Get cart (current user)
app.MapGet("/api/cart", async (
    HttpContext ctx,
    AppDb db) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null)
        return Results.Unauthorized();

    var items = await (
        from c in db.CartItems.AsNoTracking()
        join p in db.Products.AsNoTracking()
            on c.ProductId equals p.ProductId
        join s in db.Suppliers.AsNoTracking()
            on c.SupplierId equals s.SupplierId
        where c.UserId == userId
        select new
        {
            CartItemId = c.CartItemId,

            ProductId = p.ProductId,
            ProductName = p.Name,
            ImageUrl = p.ImageUrl ?? "ic_product_placeholder.png",

            SupplierId = s.SupplierId,
            SupplierName = s.Name,

            Quantity = c.Quantity,
            UnitPrice = c.UnitPrice,
            CreatedAt = c.CreatedAt
        }
    ).ToListAsync();

    return Results.Ok(items);
})
.RequireAuthorization()
.WithTags("Cart");

			
			app.MapPut("/api/cart/{cartItemId:int}", async (
    HttpContext ctx,
    AppDb db,
    int cartItemId,
    UpdateCartQtyDto dto) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var item = await db.CartItems
        .FirstOrDefaultAsync(c =>
            c.CartItemId == cartItemId &&
            c.UserId == userId);

    if (item == null)
        return Results.NotFound();

    if (dto.Quantity <= 0)
        return Results.BadRequest(new { error = "Quantity must be > 0" });

    item.Quantity = dto.Quantity;


    await db.SaveChangesAsync();
    return Results.Ok(item);
})
.RequireAuthorization()
.WithTags("Cart");

app.MapDelete("/api/cart/{cartItemId:int}", async (
    HttpContext ctx,
    AppDb db,
    int cartItemId) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var item = await db.CartItems
        .FirstOrDefaultAsync(c =>
            c.CartItemId == cartItemId &&
            c.UserId == userId);

    if (item == null)
        return Results.NotFound();

    db.CartItems.Remove(item);
    await db.SaveChangesAsync();

    return Results.Ok(new { success = true });
})
.RequireAuthorization()
.WithTags("Cart");


            // Place order (Buy Now / Checkout)
            app.MapPost("/api/orders", async (
                HttpContext ctx,
                AppDb db,
                OrderCreateReq req) =>
            {
                var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId == null) return Results.Unauthorized();

                var cart = await db.CartItems
                    .Where(c => c.UserId == userId)
                    .ToListAsync();

                if (!cart.Any())
                    return Results.BadRequest(new { error = "cart empty" });

                using var tx = await db.Database.BeginTransactionAsync();
                try
                {
                    var now = DateTime.UtcNow;

                    var order = new Order
                    {
                        OrderNumber = $"ORD-{now:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}",
                        UserId = userId,
                        AddressId = req.AddressId,
                        PaymentMethodId = req.PaymentMethodId,
                        Status = OrderStatus.Processing,
                        TotalAmount = cart.Sum(c => c.Quantity * c.UnitPrice),
                        Notes = req.Notes,
                        CreatedAt = now
                    };

                    db.Orders.Add(order);
                    await db.SaveChangesAsync();

                    foreach (var c in cart)
                    {
                        db.OrderItems.Add(new OrderItem
                        {
                            OrderId = order.OrderId,
                            ProductId = c.ProductId,
                            ProductName = c.ProductName,
                            SupplierId = c.SupplierId,
                            SupplierName = c.SupplierName,
                            Quantity = c.Quantity,
                            UnitPrice = c.UnitPrice,
							                            CreatedAt = now
                        });
                    }

                    db.CartItems.RemoveRange(cart);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();

                    return Results.Created($"/api/orders/{order.OrderId}", order);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync();
                    return Results.Problem(ex.Message);
                }
            })
            .RequireAuthorization()
            .WithTags("Orders");

            // Get current user's orders
app.MapGet("/api/orders", async (
    HttpContext ctx,
    AppDb db) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var orders = await db.Orders
        .AsNoTracking()
        .Where(o => o.UserId == userId)   // ✅ string == string
        .OrderByDescending(o => o.CreatedAt)
        .Select(o => new OrderWithItemsDto
        {
            OrderId = o.OrderId,
            OrderNumber = o.OrderNumber,
             StatusCode = (int)o.Status,
            StatusName = db.OrderStatusLookup
                .Where(s => s.StatusCode == (int)o.Status)
                .Select(s => s.StatusName)
                .FirstOrDefault() ?? "Processing",

            TotalAmount = o.TotalAmount,
            CreatedAt = o.CreatedAt,

            Items = db.OrderItems
                .Where(oi => oi.OrderId == o.OrderId)
                .Select(oi => new OrderItemDto
                {
                    ProductName = oi.ProductName,
                    Quantity = oi.Quantity
                })
                .ToList()
        })
        .ToListAsync();

    return Results.Ok(orders);
})
.RequireAuthorization()
.WithTags("Orders");

app.MapPost("/api/orders/{orderId:int}/cancel", async (
    HttpContext ctx,
    AppDb db,
    int orderId) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var order = await db.Orders
        .FirstOrDefaultAsync(o =>
            o.OrderId == orderId &&
            o.UserId == userId);

    if (order == null)
        return Results.NotFound();

    if (order.Status != OrderStatus.Processing)
        return Results.BadRequest(new { error = "Only processing orders can be cancelled" });

    order.Status = OrderStatus.Cancelled;
    order.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    return Results.Ok(new { success = true });
})
.RequireAuthorization()
.WithTags("Orders");
	

            // Request return
app.MapPost("/api/orders/{orderId:int}/request-return", async (
    HttpContext ctx,
    AppDb db,
    int orderId) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var order = await db.Orders
        .FirstOrDefaultAsync(o =>
            o.OrderId == orderId &&
            o.UserId == userId);

    if (order == null)
        return Results.NotFound();

    if (order.Status != OrderStatus.Delivered)
        return Results.BadRequest(new { error = "Only delivered orders can be returned" });

    order.Status = OrderStatus.ReturnRequested;
    order.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    return Results.Ok(new { success = true });
})
.RequireAuthorization()
.WithTags("Orders");


            // User addresses
            // ============================
            // USER ADDRESSES
            // ============================

            // GET addresses
            app.MapGet("/api/users/addresses", async (
                HttpContext ctx,
                AppDb db) =>
            {
                var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId == null) return Results.Unauthorized();

                var addrs = await db.Addresses
                    .AsNoTracking()
                    .Where(a => a.UserId == userId)
                    .OrderByDescending(a => a.IsDefault)
                    .ThenByDescending(a => a.CreatedAt)
                    .ToListAsync();

                return Results.Ok(addrs);
            })
            .RequireAuthorization()
            .WithTags("Users");

            // CREATE address
app.MapPost("/api/users/addresses", async (
    HttpContext ctx,
    AppDb db,
    AddressCreateUpdateDto dto) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null)
        return Results.Unauthorized();

    var address = new Address
    {
        UserId = userId,
        Title = dto.Title,
        FullAddress = dto.FullAddress,
        City = dto.City,
        State = dto.State,
        PostalCode = dto.PostalCode,
        Country = dto.Country,
        Phone = dto.Phone,
        IsDefault = dto.IsDefault,
        CreatedAt = DateTime.UtcNow
    };

    db.Addresses.Add(address);
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/addresses/{address.AddressId}", address);
})
.RequireAuthorization()
.WithTags("Users");


            // UPDATE address
app.MapPut("/api/users/addresses/{id:int}", async (
    HttpContext ctx,
    AppDb db,
    int id,
    AddressCreateUpdateDto dto) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null)
        return Results.Unauthorized();

    var address = await db.Addresses
        .FirstOrDefaultAsync(a => a.AddressId == id && a.UserId == userId);

    if (address == null)
        return Results.NotFound();

    address.Title = dto.Title;
    address.FullAddress = dto.FullAddress;
    address.City = dto.City;
    address.State = dto.State;
    address.PostalCode = dto.PostalCode;
    address.Country = dto.Country;
    address.Phone = dto.Phone;
    address.IsDefault = dto.IsDefault;
    address.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    return Results.Ok(address);
})
.RequireAuthorization()
.WithTags("Users");


            // ============================
            // PAYMENT METHODS
            // ============================

            // GET payment methods
            app.MapGet("/api/users/paymentmethods", async (
                HttpContext ctx,
                AppDb db) =>
            {
                var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId == null) return Results.Unauthorized();

                var pms = await db.PaymentMethods
                    .AsNoTracking()
                    .Where(pm => pm.UserId == userId)
                    .OrderByDescending(pm => pm.IsDefault)
                    .ThenByDescending(pm => pm.CreatedAt)
                    .ToListAsync();

                return Results.Ok(pms);
            })
            .RequireAuthorization()
            .WithTags("Users");

            // CREATE payment method
app.MapPost("/api/users/paymentmethods", async (
    HttpContext ctx,
    AppDb db,
    PaymentMethodCreateUpdateDto dto) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    // unset old default if needed
    if (dto.IsDefault)
    {
        await db.PaymentMethods
            .Where(p => p.UserId == userId && p.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));
    }

    var pm = new PaymentMethod
    {
        UserId = userId,
        Label = dto.Label,
        Provider = dto.Provider,
        Token = dto.Token,
        IsDefault = dto.IsDefault,
        CreatedAt = DateTime.UtcNow
    };

    db.PaymentMethods.Add(pm);
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/paymentmethods/{pm.PaymentMethodId}", pm);
})
.RequireAuthorization()
.WithTags("Users");


            // UPDATE payment method
app.MapPut("/api/users/paymentmethods/{id:int}", async (
    HttpContext ctx,
    AppDb db,
    int id,
    PaymentMethodCreateUpdateDto dto) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId == null) return Results.Unauthorized();

    var pm = await db.PaymentMethods
        .FirstOrDefaultAsync(p => p.PaymentMethodId == id && p.UserId == userId);

    if (pm == null) return Results.NotFound();

    if (dto.IsDefault)
    {
        await db.PaymentMethods
            .Where(p => p.UserId == userId && p.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));
    }

    pm.Label = dto.Label;
    pm.Provider = dto.Provider;
    pm.Token = dto.Token;
    pm.IsDefault = dto.IsDefault;
    pm.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    return Results.Ok(pm);
})
.RequireAuthorization()
.WithTags("Users");


			// DELETE address
			app.MapDelete("/api/users/addresses/{id:int}", async (
				HttpContext ctx,
				AppDb db,
				int id) =>
			{
				var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
				if (userId == null) return Results.Unauthorized();

				var address = await db.Addresses
					.FirstOrDefaultAsync(a => a.AddressId == id && a.UserId == userId);

				if (address == null)
					return Results.NotFound();

				db.Addresses.Remove(address);
				await db.SaveChangesAsync();

				return Results.Ok(new { success = true });
			})
			.RequireAuthorization()
			.WithTags("Users");

			// DELETE payment method
			app.MapDelete("/api/users/paymentmethods/{id:int}", async (
				HttpContext ctx,
				AppDb db,
				int id) =>
			{
				var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
				if (userId == null) return Results.Unauthorized();

				var pm = await db.PaymentMethods
					.FirstOrDefaultAsync(p => p.PaymentMethodId == id && p.UserId == userId);

				if (pm == null)
					return Results.NotFound();

				db.PaymentMethods.Remove(pm);
				await db.SaveChangesAsync();

				return Results.Ok(new { success = true });
			})
			.RequireAuthorization()
			.WithTags("Users");


        }
		
		

        // DTO
        public record OrderCreateReq(
            int? AddressId,
            int? PaymentMethodId,
            string? Notes);
    }
	
	public record UpdateCartQtyDto(int Quantity);

}
