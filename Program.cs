// Program.cs - conventional Program.Main entrypoint with corrected inventory/audit support
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ultivision.Modules.Products;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

public static partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ensure "DefaultConnection" is present in appsettings.json
        builder.Services.AddDbContext<AppDb>(opt =>
            opt.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
		
		// -------------------- AUTH + JWT --------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)
            ),

            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();


        var app = builder.Build();

        // ensure wwwroot/images exists for static image hosting
        var wwwRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        if (!Directory.Exists(wwwRoot)) Directory.CreateDirectory(wwwRoot);
        var imagesDir = Path.Combine(wwwRoot, "images");
        if (!Directory.Exists(imagesDir)) Directory.CreateDirectory(imagesDir);

        app.UseStaticFiles(); // serves files from wwwroot (images under /images/)
		// 🔐 AUTH MIDDLEWARE (CRITICAL)
		app.UseAuthentication();
		app.UseAuthorization();

        // Debug-safe image endpoint: serves files from wwwroot/images and logs missing files
        app.MapGet("/images/{fileName}", async (string fileName, HttpRequest req) =>
        {
            var imagesDirLocal = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images");
            var filePath = Path.Combine(imagesDirLocal, fileName);

            // normalize path to avoid directory traversal
            filePath = Path.GetFullPath(filePath);
            if (!filePath.StartsWith(Path.GetFullPath(imagesDirLocal)))
                return Results.BadRequest(new { error = "invalid filename" });

            if (!System.IO.File.Exists(filePath))
            {
                Console.WriteLine($"[Images] File not found: {filePath}");
                return Results.NotFound(new { error = "file not found", file = fileName });
            }

            // content-type guess
            var contentType = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                            : fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
                            : "application/octet-stream";

            var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
            return Results.File(bytes, contentType);
        }).WithName("DebugImageServe");


        // ------------------------
        // Helper: resolve main image URL for an apartment
        // ------------------------
        async Task<string> ResolveMainImageUrlAsync(AppDb db, string apartmentId, HttpRequest req)
        {
            string baseUrl = $"{req.Scheme}://{req.Host}/images/";
            if (string.IsNullOrWhiteSpace(apartmentId)) return baseUrl + "default.jpg";

            // try to find ApartmentImages main image first (IsMain -> SortOrder)
            var mainImg = await db.ApartmentImages
                .AsNoTracking()
                .Where(i => i.ApartmentId == apartmentId)
                .OrderByDescending(i => i.IsMain)
                .ThenBy(i => i.SortOrder)
                .FirstOrDefaultAsync();

            if (mainImg != null)
            {
                if (!string.IsNullOrWhiteSpace(mainImg.Url))
                {
                    return mainImg.Url;
                }
                else if (!string.IsNullOrWhiteSpace(mainImg.FileName))
                {
                    // treat FileName as file stored under wwwroot/images/
                    if (mainImg.FileName.StartsWith("/"))
                        return $"{req.Scheme}://{req.Host}{mainImg.FileName}";
                    return baseUrl + mainImg.FileName;
                }
            }

            // fallback: Apartments.DefaultImage
            var apt = await db.Apartments.AsNoTracking().FirstOrDefaultAsync(a => a.ApartmentId == apartmentId);
            if (apt != null && !string.IsNullOrWhiteSpace(apt.DefaultImage))
            {
                if (apt.DefaultImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return apt.DefaultImage;
                if (apt.DefaultImage.StartsWith("/"))
                    return $"{req.Scheme}://{req.Host}{apt.DefaultImage}";
                return baseUrl + apt.DefaultImage;
            }

            // final fallback
            return baseUrl + "default.jpg";
        }

        // returns a normalized stored image path (prefer leading "/images/..." or "/...") 
        string NormalizeImageForStorage(string? img)
        {
            if (string.IsNullOrWhiteSpace(img)) return string.Empty;

            // already an absolute URL -> prefer storing the path part (so host independent)
            if (img.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                img.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var u = new Uri(img);
                    // keep the path and query (e.g. "/images/foo.jpg" or "/images/foo.jpg?v=1")
                    return u.PathAndQuery;
                }
                catch
                {
                    // fallback: leave as-is
                    return img;
                }
            }

            // if already starts with a slash - keep it ("/images/abc.jpg" or "/abc.jpg")
            if (img.StartsWith("/")) return img;

            // otherwise treat it as filename and store under /images/
            return "/images/" + img;
        }

        // when sending to clients, convert stored path/filename to full URL using request host
        string ResolveStoredImageToUrl(string storedImage, HttpRequest req)
        {
            string baseUrl = $"{req.Scheme}://{req.Host}";
            if (string.IsNullOrWhiteSpace(storedImage)) return baseUrl + "/images/default.jpg";

            if (storedImage.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                storedImage.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return storedImage;
            }

            if (storedImage.StartsWith("/"))
                return $"{baseUrl}{storedImage}";

            // fallback: assume storedImage is filename under /images/
            return $"{baseUrl}/images/{storedImage}";
        }

        // ------------------------
        // Inventory helper: decrement per-night RoomInventory + update master Rooms.AvailableCount and create InventoryAudit entries
        // ------------------------
        async Task DecrementInventoryForBooking(AppDb db, string roomId, DateTime startDate, DateTime endDate, int qty, Guid bookingId, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return;
            if (qty <= 0) qty = 1;

            // process each night
            for (var dt = startDate.Date; dt < endDate.Date; dt = dt.AddDays(1))
            {
                try
                {
                    // ensure an inventory row exists
                    var inv = await db.RoomInventory.FirstOrDefaultAsync(ri => ri.RoomId == roomId && ri.Date == dt);
                    // read master for cap
                    var rm = await db.Rooms.FirstOrDefaultAsync(r => r.RoomId == roomId);
                    int cap = rm?.RoomCount ?? 0;

                    if (inv == null)
                    {
                        // initialize from cap if present, otherwise from qty
                        int initial = cap > 0 ? cap : qty;
                        inv = new RoomInventory
                        {
                            RoomId = roomId,
                            Date = dt,
                            AvailableCount = initial,
                            CreatedAt = DateTime.UtcNow
                        };
                        db.RoomInventory.Add(inv);
                        // Save so EF tracks the new row for subsequent update
                        await db.SaveChangesAsync();
                    }

                    var oldAvail = inv.AvailableCount;
                    var updated = oldAvail - qty;
                    if (cap > 0 && updated < 0) updated = 0;
                    inv.AvailableCount = updated;
                    db.RoomInventory.Update(inv);

                    // audit
                    var audit = new InventoryAudit
                    {
                        RoomId = roomId,
                        Date = dt,
                        OldAvailable = oldAvail,
                        NewAvailable = updated,
                        Delta = -qty,
                        Reason = $"Booked {bookingId}",
                        BookingId = bookingId.ToString(),
                        ChangedAt = DateTime.UtcNow
                    };
                    await db.InventoryAudits.AddAsync(audit);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to decrement inventory for {Room} on {Date}", roomId, dt);
                }
            }

            // update master Rooms.AvailableCount as best-effort
            try
            {
                var roomMaster = await db.Rooms.FirstOrDefaultAsync(r => r.RoomId == roomId);
                if (roomMaster != null)
                {
                    int roomCount = roomMaster.RoomCount ?? 0;
                    int oldM = roomMaster.AvailableCount;
                    // if AvailableCount is zero and RoomCount exists, prefer RoomCount as initial
                    if (oldM == 0 && roomCount > 0) oldM = roomCount;
                    int updatedM = Math.Max(0, oldM - qty);
                    roomMaster.AvailableCount = updatedM;
                    db.Rooms.Update(roomMaster);

                    var auditM = new InventoryAudit
                    {
                        RoomId = roomId,
                        Date = null,
                        OldAvailable = oldM,
                        NewAvailable = updatedM,
                        Delta = -qty,
                        Reason = $"Booked (master) {bookingId}",
                        BookingId = bookingId.ToString(),
                        ChangedAt = DateTime.UtcNow
                    };
                    await db.InventoryAudits.AddAsync(auditM);
                }
            }
            catch (Exception ex)
            {
                // best-effort only
            }
        }

        // ------------------------
        // AUTH (basic login/register used by your client - no JWT)
        // ------------------------
app.MapPost("/api/auth/login", async (
    [FromBody] LoginReq req,
    AppDb db,
    IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "username and password required" });

    var user = await db.UserTestData.FirstOrDefaultAsync(u => u.Username == req.Username);
    if (user == null) return Results.Unauthorized();

    bool ok = !string.IsNullOrWhiteSpace(user.PasswordHash)
        ? BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash)
        : user.PlainPassword == req.Password;

    if (!ok) return Results.Unauthorized();

    // 🔐 CREATE JWT
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username ?? ""),
        new Claim(ClaimTypes.Role, "User")
    };

    var key = new SymmetricSecurityKey(
        Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: config["Jwt:Issuer"],
        audience: config["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(
            int.Parse(config["Jwt:ExpiryMinutes"]!)),
        signingCredentials: creds
    );

    var jwt = new JwtSecurityTokenHandler().WriteToken(token);

    return Results.Ok(new
    {
        token = jwt,
        userId = user.Id,
        displayName = user.DisplayName,
        email = user.Email
    });
})
.WithTags("Auth")
.WithName("Login");

app.MapGet("/api/addresses", 
[Microsoft.AspNetCore.Authorization.Authorize]
async (HttpContext ctx, AppDb db) =>
{
    var userId = int.Parse(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    return await db.Addresses
        .Where(a => a.UserId == userId.ToString())
        .ToListAsync();
});


        app.MapPost("/api/auth/register", async ([FromBody] RegisterReq req, AppDb db) =>
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "username and password required" });

            if (!string.IsNullOrWhiteSpace(req.Email) &&
                !System.Text.RegularExpressions.Regex.IsMatch(req.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                return Results.BadRequest(new { error = "invalid email" });

            if (await db.UserTestData.AnyAsync(u => u.Username == req.Username))
                return Results.Conflict(new { error = "username already exists" });

            string hash = BCrypt.Net.BCrypt.HashPassword(req.Password);

            var user = new UserTestData
            {
                Username = req.Username,
                PasswordHash = hash,
                DisplayName = req.Name,
                Email = req.Email
            };

            db.UserTestData.Add(user);
            await db.SaveChangesAsync();

            return Results.Created($"/api/users/{user.Id}", new { success = true, id = user.Id });
        }).WithTags("Auth").WithName("Register");

        app.MapPost("/api/auth/forgot", async ([FromBody] ForgotReq req, AppDb db) =>
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "email required" });
            var user = await db.UserTestData.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (user == null) return Results.NotFound(new { error = "email not found" });
            user.Otp = "450650"; // dev OTP
            await db.SaveChangesAsync();
            return Results.Ok(new { success = true });
        }).WithTags("Auth").WithName("ForgotPassword");

        app.MapPost("/api/auth/reset", async ([FromBody] ResetReq req, AppDb db) =>
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Otp) || string.IsNullOrWhiteSpace(req.NewPassword))
                return Results.BadRequest(new { error = "email, otp, newPassword required" });

            var user = await db.UserTestData.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (user == null) return Results.NotFound(new { error = "email not found" });
            if (user.Otp != req.Otp) return Results.BadRequest(new { error = "invalid otp" });

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
            user.PlainPassword = null;
            user.Otp = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { success = true });
        }).WithTags("Auth").WithName("ResetPassword");

        // ------------------------
        // APARTMENTS: list + details (with images resolved from ApartmentImages)
        // ------------------------
        app.MapGet("/api/bookings/apartments", [Microsoft.AspNetCore.Authorization.AllowAnonymous] async (AppDb db, HttpRequest req) =>
        {
            try
            {
                var apartments = await db.Apartments.AsNoTracking().ToListAsync();
                var result = new List<object>(apartments.Count);

                foreach (var a in apartments)
                {
                    var aptId = a?.ApartmentId ?? string.Empty;

                    // resolve main image using helper
                    string imageUrl = await ResolveMainImageUrlAsync(db, aptId, req);

                    result.Add(new
                    {
                        ApartmentId = aptId,
                        Title = a?.Title ?? string.Empty,
                        Subtitle = a?.Subtitle ?? string.Empty,
                        Location = a?.Location ?? string.Empty,
                        DefaultImage = imageUrl,
                        Rating = a?.Rating
                    });
                }

                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Server error while listing apartments", detail: ex.ToString(), statusCode: 500);
            }
        }).WithName("GetApartments");

        // GET apartment details (with gallery) — uses ApartmentImages for gallery ordering and main image fallback
        app.MapGet("/api/bookings/apartments/{apartmentId}", [Microsoft.AspNetCore.Authorization.AllowAnonymous] async (string apartmentId, AppDb db, HttpRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(apartmentId))
                return Results.BadRequest(new { error = "apartmentId required" });

            var apt = await db.Apartments
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.ApartmentId == apartmentId);

            if (apt == null)
                return Results.NotFound(new { error = "Apartment not found", apartmentId });

            string baseUrl = $"{req.Scheme}://{req.Host}/images/";

            var images = await db.ApartmentImages
                .AsNoTracking()
                .Where(i => i.ApartmentId == apartmentId)
                .OrderByDescending(i => i.IsMain)
                .ThenBy(i => i.SortOrder)
                .ToListAsync();

            var urls = new List<string>();
            foreach (var img in images)
            {
                if (!string.IsNullOrWhiteSpace(img?.Url))
                    urls.Add(img.Url);
                else if (!string.IsNullOrWhiteSpace(img?.FileName))
                    urls.Add(img.FileName.StartsWith("/") ? $"{req.Scheme}://{req.Host}{img.FileName}" : baseUrl + img.FileName);
            }

            // fallbacks
            if (urls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(apt.DefaultImage))
                {
                    if (apt.DefaultImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        urls.Add(apt.DefaultImage);
                    else if (apt.DefaultImage.StartsWith("/"))
                        urls.Add($"{req.Scheme}://{req.Host}{apt.DefaultImage}");
                    else
                        urls.Add(baseUrl + apt.DefaultImage);
                }
                else
                {
                    urls.Add(baseUrl + "default.jpg");
                }
            }

            var response = new
            {
                ApartmentId = apt.ApartmentId ?? string.Empty,
                Title = apt.Title ?? string.Empty,
                Subtitle = apt.Subtitle ?? string.Empty,
                Description = apt.Description ?? string.Empty,
                Location = apt.Location ?? string.Empty,
                Address = apt.Address ?? string.Empty,
                DefaultImage = urls.FirstOrDefault(),
                Images = urls.ToArray(),
                Rating = apt.Rating,
                Amenities = apt.Amenities ?? string.Empty,
                AmenitiesList = string.IsNullOrWhiteSpace(apt.Amenities)
                    ? Array.Empty<string>()
                    : apt.Amenities.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray()
            };

            return Results.Ok(response);
        }).WithName("GetApartmentDetails");

        // ------------------------
        // ROOMS (basic list)
        // ------------------------
        app.MapGet("/api/bookings/apartments/{apartmentId}/rooms", async (string apartmentId, AppDb db) =>
        {
            var roomsQuery = db.Rooms.AsNoTracking().Where(r => r.ApartmentId == apartmentId);

            var rooms = await roomsQuery
                .Select(r => new {
                    RoomId = r.RoomId,
                    Name = r.Name,
                    BedInfo = r.BedInfo,
                    MaxPeople = r.MaxPeople,
                    PricePerNight = r.PricePerNight,
                    IsInstantBook = r.IsInstantBook,
                    ShortAmenities = r.ShortAmenities,
                    RoomCount = r.RoomCount // expose RoomCount if present
                }).ToListAsync();

            return Results.Ok(rooms);
        }).WithName("GetRoomsForApartment");

        // ------------------------
        // ROOMS AVAILABILITY FOR APARTMENT (new endpoint)
        // Returns each room with RoomCount and AvailableCount for the requested date range
        // Query params: start, end (ISO date string or yyyy-MM-dd)
        // ------------------------
        app.MapGet("/api/bookings/apartments/{apartmentId}/rooms/availability", [Microsoft.AspNetCore.Authorization.AllowAnonymous] async (string apartmentId, DateTime start, DateTime end, AppDb db) =>
        {
            if (string.IsNullOrWhiteSpace(apartmentId)) return Results.BadRequest(new { error = "apartmentId required" });
            if (end < start) return Results.BadRequest(new { error = "end must be >= start" });

            var rooms = await db.Rooms
                .AsNoTracking()
                .Where(r => r.ApartmentId == apartmentId)
                .Select(r => new
                {
                    r.RoomId,
                    r.Name,
                    r.BedInfo,
                    MaxPeople = r.MaxPeople ?? 1,
                    PricePerNight = r.PricePerNight ?? 0m,
                    IsInstantBook = r.IsInstantBook ?? false,
                    ShortAmenities = r.ShortAmenities ?? string.Empty,
                    RoomCount = r.RoomCount ?? 1
                }).ToListAsync();

            var result = new List<object>();

            foreach (var r in rooms)
            {
                // sum blocked quantity overlapping requested range (RoomAvailability is canonical)
                var blockedCount = await db.RoomAvailability
                    .AsNoTracking()
                    .Where(a => a.RoomId == r.RoomId && a.IsBlocked &&
                                a.StartDate <= end && a.EndDate >= start)
                    .SumAsync(a => (int?)a.Quantity) ?? 0;

                // We treat RoomAvailability as canonical occupancy (blocks include bookings),
                // so we DO NOT add Bookings here to avoid double-counting.
                int bookedCount = 0;

                int available = Math.Max(0, (r.RoomCount) - (blockedCount + bookedCount));

                result.Add(new
                {
                    r.RoomId,
                    r.Name,
                    r.BedInfo,
                    r.MaxPeople,
                    PricePerNight = r.PricePerNight,
                    r.IsInstantBook,
                    r.ShortAmenities,
                    RoomCount = r.RoomCount,
                    AvailableCount = available
                });
            }

            return Results.Ok(result);
        }).WithName("GetRoomsWithAvailability");

        // ------------------------
        // AVAILABILITY (per-room simple check)
        // ------------------------
        app.MapGet("/api/bookings/rooms/{roomId}/available", async (string roomId, DateTime start, DateTime end, AppDb db) =>
        {
            if (end < start) return Results.BadRequest(new { error = "end must be >= start" });

            // get room count
            var roomEntity = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.RoomId == roomId);
            int roomCount = (roomEntity?.RoomCount ?? 1);

            // sum blocked quantity overlapping (RoomAvailability is canonical)
            var blockedTotal = await db.RoomAvailability
                .AsNoTracking()
                .Where(a => a.RoomId == roomId && a.IsBlocked &&
                            a.StartDate <= end && a.EndDate >= start)
                .SumAsync(a => (int?)a.Quantity) ?? 0;

            // do not double-count bookings here; blockedTotal includes bookings
            var bookedTotal = 0;

            var available = (roomCount - (blockedTotal + bookedTotal)) > 0;
            return Results.Ok(new { available, roomCount, blockedTotal, availableCount = Math.Max(0, roomCount - (blockedTotal + bookedTotal)) });

        }).WithName("CheckRoomAvailability");

        // ------------------------
        // BOOKINGS: by user, create (transactional)
        // ------------------------
        app.MapGet("/api/bookings/user/{userId:int}", async (int userId, AppDb db, HttpRequest req) =>
        {
            if (userId <= 0) return Results.BadRequest(new { error = "invalid userId" });

            var list = await db.Bookings.AsNoTracking()
                .Where(b => b.UserTestDataId == userId)
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => new {
                    b.BookingId,
                    b.BookingGroupId,
                    b.ApartmentId,
                    b.RoomId,
                    b.ApartmentTitle,
                    b.ApartmentImage,
                    b.RoomName,
                    b.StartDate,
                    b.EndDate,
                    b.TotalPrice,
                    b.Guests,
                    b.Status,
                    b.CancelReason,
                    b.CreatedAt,
                    // Denormalized customer fields stored on booking (prefer these for display)
                    CustomerId = b.CustomerId,
                    CustomerName = b.CustomerName,
                    CustomerEmail = b.CustomerEmail,
                    CustomerPhone = b.CustomerPhone,
                    // apartment address/location
                    Apartment = db.Apartments.AsNoTracking()
                                  .Where(a => a.ApartmentId == b.ApartmentId)
                                  .Select(a => new { a.Address, a.Location }).FirstOrDefault()
                })
                .ToListAsync();

            var result = list.Select(x => new {
                x.BookingId,
                BookingGroupId = x.BookingGroupId,
                x.ApartmentId,
                x.RoomId,
                x.ApartmentTitle,
                // resolve stored image path to absolute URL using current request host
                ApartmentImage = ResolveStoredImageToUrl(x.ApartmentImage ?? string.Empty, req),
                x.RoomName,
                x.StartDate,
                x.EndDate,
                x.TotalPrice,
                x.Guests,
                x.Status,
                x.CancelReason,
                x.CreatedAt,
                ApartmentAddress = (x.Apartment != null ? ((x.Apartment.Address ?? "") + (string.IsNullOrWhiteSpace(x.Apartment.Location) ? "" : (", " + x.Apartment.Location))) : null),
                ApartmentLocation = x.Apartment?.Location,
                ApartmentAddressOnly = x.Apartment?.Address,
                CustomerId = x.CustomerId,
                // these now come from the booking row (denormalized) and will show even if CustomerId is null
                CustomerName = x.CustomerName,
                CustomerEmail = x.CustomerEmail,
                CustomerPhone = x.CustomerPhone
            }).ToList();

            return Results.Ok(result);
        })
        .WithName("GetBookingsByUser");

        // GET booking groups for a user: returns BookingGroupId -> TotalPrice, Currency, CreatedAt
        app.MapGet("/api/bookinggroups/user/{userId:int}", async (int userId, AppDb db) =>
        {
            if (userId <= 0) return Results.BadRequest(new { error = "invalid userId" });

            var groups = await db.BookingGroups
                .AsNoTracking()
                .Where(g => g.UserTestDataId == userId)
                .OrderByDescending(g => g.CreatedAt)
                .Select(g => new {
                    BookingGroupId = g.BookingGroupId,
                    TotalPrice = g.TotalPrice,
                    Currency = g.Currency,
                    CreatedAt = g.CreatedAt
                }).ToListAsync();

            return Results.Ok(groups);
        }).WithName("GetBookingGroupsByUser");

        // Create single booking (transactional)
        app.MapPost("/api/bookings", async ([FromBody] BookingCreateReq req, AppDb db, HttpRequest httpReq) =>
        {
            try
            {
                httpReq.EnableBuffering();
                using var sr = new StreamReader(httpReq.Body, leaveOpen: true);
                httpReq.Body.Position = 0;
                var bodyText = await sr.ReadToEndAsync();
                httpReq.Body.Position = 0;
                System.Diagnostics.Debug.WriteLine("Incoming /api/bookings body: " + bodyText);
            }
            catch { }

            if (req == null || string.IsNullOrWhiteSpace(req.ApartmentId) || string.IsNullOrWhiteSpace(req.RoomId) || req.StartDate == default || req.EndDate == default)
                return Results.BadRequest(new { error = "apartmentId, roomId, startDate, endDate required" });

            if (req.EndDate < req.StartDate) return Results.BadRequest(new { error = "endDate must be >= startDate" });

            var roomEntity = await db.Rooms.FirstOrDefaultAsync(r => r.ApartmentId == req.ApartmentId && r.RoomId == req.RoomId);
            if (roomEntity == null)
            {
                roomEntity = await db.Rooms
                    .Where(r => r.ApartmentId == req.ApartmentId)
                    .FirstOrDefaultAsync(r =>
                        (!string.IsNullOrEmpty(r.RoomId) && string.Equals(r.RoomId, req.RoomId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(r.Name) && string.Equals(r.Name, req.RoomId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(r.RoomId) && r.RoomId.EndsWith(req.RoomId, StringComparison.OrdinalIgnoreCase))
                    );
            }
            if (roomEntity == null) return Results.BadRequest(new { error = "Room not found for apartment. Please select a valid room." });

            var roomIdUsed = roomEntity.RoomId;

            var conflict = await db.Bookings.AnyAsync(b =>
                b.RoomId == roomIdUsed &&
                b.Status != 3 &&
                b.StartDate <= req.EndDate &&
                b.EndDate >= req.StartDate);
            if (conflict) return Results.Conflict(new { error = "room not available for requested dates" });

            await using var tx = await db.Database.BeginTransactionAsync();

            Customer? guestCustomer = null;
            int? userTestDataId = req.UserTestDataId;

            if (userTestDataId.HasValue)
            {
                var user = await db.UserTestData.FirstOrDefaultAsync(u => u.Id == userTestDataId.Value);
                if (user == null) { await tx.RollbackAsync(); return Results.BadRequest(new { error = "invalid userTestDataId" }); }

                if (string.IsNullOrEmpty(user.CustomerType))
                {
                    user.CustomerType = "Registered";
                    db.UserTestData.Update(user);
                    await db.SaveChangesAsync();
                }

                string externalUserId = user.Id.ToString();
                var mapped = await db.Customers.FirstOrDefaultAsync(c => c.ExternalUserId == externalUserId);

                if (mapped == null && !string.IsNullOrWhiteSpace(user.Email))
                    mapped = await db.Customers.FirstOrDefaultAsync(c => c.Email == user.Email);

                if (mapped == null)
                {
                    mapped = new Customer
                    {
                        ExternalUserId = externalUserId,
                        FullName = user.DisplayName ?? user.Username ?? ("User" + externalUserId),
                        Email = user.Email,
                        Phone = null,
                        CreatedAt = DateTime.UtcNow
                    };
                    db.Customers.Add(mapped);
                    await db.SaveChangesAsync();
                }

                guestCustomer = mapped;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(req.CustomerEmail))
                {
                    guestCustomer = await db.Customers.FirstOrDefaultAsync(c => c.Email == req.CustomerEmail);
                    if (guestCustomer == null)
                    {
                        guestCustomer = new Customer
                        {
                            FullName = req.CustomerName ?? "Guest",
                            Email = req.CustomerEmail,
                            Phone = req.CustomerPhone,
                            CreatedAt = DateTime.UtcNow
                        };
                        db.Customers.Add(guestCustomer);
                        await db.SaveChangesAsync();
                    }
                }
                else if (!string.IsNullOrWhiteSpace(req.CustomerName))
                {
                    guestCustomer = new Customer
                    {
                        FullName = req.CustomerName,
                        Email = req.CustomerEmail,
                        Phone = req.CustomerPhone,
                        CreatedAt = DateTime.UtcNow
                    };
                    db.Customers.Add(guestCustomer);
                    await db.SaveChangesAsync();
                }
            }

            var booking = new Booking
            {
                ApartmentId = req.ApartmentId,
                RoomId = roomIdUsed,
                CustomerId = guestCustomer?.CustomerId,
                UserTestDataId = userTestDataId,
                StartDate = req.StartDate,
                EndDate = req.EndDate,
                Guests = req.Guests,
                TotalPrice = req.TotalPrice,
                ApartmentTitle = req.ApartmentTitle,
                ApartmentImage = NormalizeImageForStorage(req.ApartmentImage),
                RoomName = req.RoomName ?? roomEntity.Name,
                Status = 0,
                CreatedAt = DateTime.UtcNow,
                // new fields:
                CustomerName = string.IsNullOrWhiteSpace(req.CustomerName) ? guestCustomer?.FullName : req.CustomerName,
                CustomerEmail = string.IsNullOrWhiteSpace(req.CustomerEmail) ? guestCustomer?.Email : req.CustomerEmail,
                CustomerPhone = string.IsNullOrWhiteSpace(req.CustomerPhone) ? guestCustomer?.Phone : req.CustomerPhone,

                // IMPORTANT: set booking quantity (default to 1 if request doesn't carry it)
                Quantity = Math.Max(1, req.Quantity)
            };

            db.Bookings.Add(booking);

            var block = new RoomAvailability
            {
                RoomId = roomIdUsed,
                StartDate = req.StartDate,
                EndDate = req.EndDate,
                IsBlocked = true,
                Quantity = Math.Max(1, req.Quantity),     // <-- set blocked quantity
                Note = "Blocked for booking " + booking.BookingId,
                CreatedAt = DateTime.UtcNow
            };
            db.RoomAvailability.Add(block);

            // decrement per-night inventory + master + audit using helper
            await db.SaveChangesAsync(); // persist booking & block first so booking id and block exist
            await DecrementInventoryForBooking(db, roomIdUsed, req.StartDate, req.EndDate, Math.Max(1, req.Quantity), booking.BookingId);

            Payment? payment = null;
            if (req.TotalPrice > 0)
            {
                payment = new Payment
                {
                    BookingId = booking.BookingId,
                    Amount = req.TotalPrice,
                    Currency = "SAR",
                    Method = "card",
                    TransactionId = Guid.NewGuid().ToString(),
                    Status = "Paid",
                    CreatedAt = DateTime.UtcNow
                };
                db.Payments.Add(payment);
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            var created = new
            {
                booking.BookingId,
                booking.ApartmentId,
                booking.RoomId,
                booking.StartDate,
                booking.EndDate,
                booking.TotalPrice,
                booking.Guests,
                Customer = guestCustomer == null ? null : new { guestCustomer.CustomerId, guestCustomer.FullName, guestCustomer.Email, guestCustomer.Phone },
                Payments = payment == null ? Array.Empty<object>() : new[] { new { payment.PaymentId, payment.Amount, payment.Currency, payment.Method, payment.TransactionId, payment.Status, payment.CreatedAt } }
            };

            return Results.Created($"/api/bookings/{booking.BookingId}", created);
        }).WithName("CreateBooking");

        // =============================================================
        // MULTI-ROOM BOOKING ENDPOINT
        // =============================================================
        app.MapPost("/api/bookings/multi", async ([FromBody] MultiBookingCreateReq req, AppDb db, HttpRequest httpReq) =>
        {
            if (req == null || req.Rooms == null || req.Rooms.Count == 0)
                return Results.BadRequest(new { error = "No rooms provided" });

            if (req.EndDate < req.StartDate)
                return Results.BadRequest(new { error = "Invalid date range" });

            int nights = Math.Max(1, (req.EndDate - req.StartDate).Days);

            // ---------- Verify Availability For All Rooms ----------
            foreach (var r in req.Rooms)
            {
                // Use RoomAvailability as canonical occupancy (blocks include bookings)
                var blockedTotal = await db.RoomAvailability
                    .AsNoTracking()
                    .Where(a => a.RoomId == r.RoomId && a.IsBlocked &&
                                a.StartDate <= req.EndDate && a.EndDate >= req.StartDate)
                    .SumAsync(a => (int?)a.Quantity) ?? 0;

                // Do not add Bookings here (to avoid double counting)
                var bookedTotal = 0;

                var roomEntity = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.RoomId == r.RoomId);
                int roomCount = (roomEntity?.RoomCount ?? 1);

                if ((blockedTotal + bookedTotal + r.Quantity) > roomCount)
                    return Results.Conflict(new { error = $"Not enough availability for room {r.RoomId}" });
            }

            // ---------- Create Group, Customer & Bookings (transactional) ----------
            await using var tx = await db.Database.BeginTransactionAsync();

            // map/create customer (reuse single-booking logic)
            Customer? guestCustomer = null;
            if (req.UserTestDataId.HasValue)
            {
                var user = await db.UserTestData.FirstOrDefaultAsync(u => u.Id == req.UserTestDataId.Value);
                if (user != null)
                {
                    if (string.IsNullOrEmpty(user.CustomerType))
                    {
                        user.CustomerType = "Registered";
                        db.UserTestData.Update(user);
                        await db.SaveChangesAsync();
                    }

                    string externalUserId = user.Id.ToString();
                    var mapped = await db.Customers.FirstOrDefaultAsync(c => c.ExternalUserId == externalUserId);
                    if (mapped == null && !string.IsNullOrWhiteSpace(user.Email))
                        mapped = await db.Customers.FirstOrDefaultAsync(c => c.Email == user.Email);

                    if (mapped == null)
                    {
                        mapped = new Customer
                        {
                            ExternalUserId = externalUserId,
                            FullName = user.DisplayName ?? user.Username ?? ("User" + externalUserId),
                            Email = user.Email,
                            Phone = null,
                            CreatedAt = DateTime.UtcNow
                        };
                        db.Customers.Add(mapped);
                        await db.SaveChangesAsync();
                    }

                    guestCustomer = mapped;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(req.CustomerEmail))
                {
                    guestCustomer = await db.Customers.FirstOrDefaultAsync(c => c.Email == req.CustomerEmail);
                    if (guestCustomer == null)
                    {
                        guestCustomer = new Customer
                        {
                            FullName = req.CustomerName ?? "Guest",
                            Email = req.CustomerEmail,
                            Phone = req.CustomerPhone,
                            CreatedAt = DateTime.UtcNow
                        };
                        db.Customers.Add(guestCustomer);
                        await db.SaveChangesAsync();
                    }
                }
                else if (!string.IsNullOrWhiteSpace(req.CustomerName))
                {
                    guestCustomer = new Customer
                    {
                        FullName = req.CustomerName,
                        Email = req.CustomerEmail,
                        Phone = req.CustomerPhone,
                        CreatedAt = DateTime.UtcNow
                    };
                    db.Customers.Add(guestCustomer);
                    await db.SaveChangesAsync();
                }
            }

            var group = new BookingGroup
            {
                BookingGroupId = Guid.NewGuid(),
                ApartmentId = req.ApartmentId,
                UserTestDataId = req.UserTestDataId,
                TotalPrice = req.TotalPrice,
                CreatedAt = DateTime.UtcNow
            };
            db.BookingGroups.Add(group);
            await db.SaveChangesAsync();

            var createdBookingIds = new List<Guid>();

            // ---------- Create Each Room Booking ----------
            foreach (var r in req.Rooms)
            {
                // Create a single booking row representing the requested quantity (recommended)
                var booking = new Booking
                {
                    BookingId = Guid.NewGuid(),
                    BookingGroupId = group.BookingGroupId,
                    ApartmentId = req.ApartmentId,
                    RoomId = r.RoomId,
                    StartDate = req.StartDate,
                    EndDate = req.EndDate,
                    Guests = req.Adults + req.Children,
                    TotalPrice = r.PricePerNight * nights * Math.Max(1, r.Quantity), // adjust if TotalPrice is per room-per-night
                    Currency = "SAR",
                    Status = 0,
                    RoomName = r.Name,
                    CreatedAt = DateTime.UtcNow,
                    CustomerName = req.CustomerName,
                    CustomerEmail = req.CustomerEmail,
                    CustomerPhone = req.CustomerPhone,
                    UserTestDataId = req.UserTestDataId,
                    ApartmentTitle = req.ApartmentTitle,
                    ApartmentImage = NormalizeImageForStorage(req.ApartmentImage),

                    CustomerId = guestCustomer?.CustomerId,
                    // If you added Quantity property to Booking, set it below:
                    Quantity = Math.Max(1, r.Quantity)
                };

                db.Bookings.Add(booking);
                await db.SaveChangesAsync();

                createdBookingIds.Add(booking.BookingId);

                // Upsert RoomAvailability: if row exists for same RoomId+dates+IsBlocked, increment Quantity; otherwise add new row with Quantity = r.Quantity
                var existingBlock = await db.RoomAvailability
                    .Where(a => a.RoomId == r.RoomId && a.IsBlocked && a.StartDate == req.StartDate && a.EndDate == req.EndDate)
                    .FirstOrDefaultAsync();

                if (existingBlock != null)
                {
                    existingBlock.Quantity += Math.Max(1, r.Quantity);
                    existingBlock.Note = $"Blocked by multi-booking {booking.BookingId} (incremented)";
                    db.RoomAvailability.Update(existingBlock);
                }
                else
                {
                    db.RoomAvailability.Add(new RoomAvailability
                    {
                        RoomId = r.RoomId,
                        StartDate = req.StartDate,
                        EndDate = req.EndDate,
                        IsBlocked = true,
                        Quantity = Math.Max(1, r.Quantity),
                        Note = $"Blocked by multi-booking {booking.BookingId}",
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await db.SaveChangesAsync();

                // decrement inventory for this booking (r.Quantity units)
                await DecrementInventoryForBooking(db, r.RoomId, req.StartDate, req.EndDate, Math.Max(1, r.Quantity), booking.BookingId);
            }

            // ---------- Create a Payment (first booking reference kept for legacy FK) ----------
            var payment = new Payment
            {
                PaymentId = Guid.NewGuid(),
                BookingId = createdBookingIds.First(),
                Amount = req.TotalPrice,
                Currency = "SAR",
                Method = "card",
                TransactionId = Guid.NewGuid().ToString(),
                Status = "Paid",
                CreatedAt = DateTime.UtcNow
            };

            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            await tx.CommitAsync();

            return Results.Created("/api/bookings/multi", new
            {
                BookingGroupId = group.BookingGroupId,
                BookingIds = createdBookingIds,
                Total = req.TotalPrice
            });
        });

        // GET booking by bookingId (guid)
        app.MapGet("/api/bookings/{bookingId}", async (Guid bookingId, AppDb db, HttpRequest req) =>
        {
            var b = await db.Bookings
                .AsNoTracking()
                .Where(x => x.BookingId == bookingId)
                .Select(x => new
                {
                    x.BookingId,
                    x.ApartmentId,
                    x.RoomId,
                    x.ApartmentTitle,
                    x.ApartmentImage,  // <--- stored value (relative path or filename)
                    x.RoomName,
                    x.StartDate,
                    x.EndDate,
                    x.Guests,
                    x.TotalPrice,
                    x.Currency,
                    x.Status,
                    x.CancelReason,
                    x.CreatedAt,
                    Customer = x.Customer == null ? null : new {
                        x.Customer.CustomerId,
                        x.Customer.FullName,
                        x.Customer.Email,
                        x.Customer.Phone
                    },
                    Payments = db.Payments.AsNoTracking()
                                .Where(p => p.BookingId == x.BookingId)
                                .Select(p => new {
                                    p.PaymentId,
                                    p.Amount,
                                    p.Currency,
                                    p.Method,
                                    p.TransactionId,
                                    p.Status,
                                    p.CreatedAt
                                }).ToArray()
                })
                .FirstOrDefaultAsync();

            if (b == null)
                return Results.NotFound(new { error = "booking not found", bookingId });

            // Build absolute URL for ApartmentImage
            string imageUrl = ResolveStoredImageToUrl(b.ApartmentImage ?? "", req);

            var response = new
            {
                b.BookingId,
                b.ApartmentId,
                b.RoomId,
                b.ApartmentTitle,
                ApartmentImage = imageUrl,   // <-- fixed URL returned to client
                b.RoomName,
                b.StartDate,
                b.EndDate,
                b.Guests,
                b.TotalPrice,
                b.Currency,
                b.Status,
                b.CancelReason,
                b.CreatedAt,
                b.Customer,
                b.Payments
            };

            return Results.Ok(response);
        }).WithName("GetBookingById");

        // Cancel booking (group-aware + per-night inventory rollback + audit)
        app.MapPost("/api/bookings/{bookingId}/cancel", async (Guid bookingId, HttpRequest httpReq, AppDb db, ILogger logger) =>
        {
            // read reason (optional)
            async Task<string> ReadReason()
            {
                try
                {
                    httpReq.EnableBuffering();
                    using var sr = new StreamReader(httpReq.Body, leaveOpen: true);
                    httpReq.Body.Position = 0;
                    var body = await sr.ReadToEndAsync();
                    httpReq.Body.Position = 0;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                            return r.GetString() ?? "Cancelled by user";
                    }
                }
                catch { /* ignore */ }
                return "Cancelled by user";
            }

            try
            {
                var reason = await ReadReason();

                // Find the booking row (strongly-typed)
                var booking = await db.Set<Booking>().FirstOrDefaultAsync(b => b.BookingId == bookingId);
                if (booking == null)
                    return Results.NotFound(new { error = "booking not found", bookingId });

                // Determine group id (if present) and gather rows to cancel
                Guid? groupId = booking.BookingGroupId;
                List<Booking> rowsToProcess;
                if (groupId.HasValue)
                {
                    rowsToProcess = await db.Set<Booking>().Where(b => b.BookingGroupId == groupId.Value).ToListAsync();
                }
                else
                {
                    rowsToProcess = new List<Booking> { booking };
                }

                if (rowsToProcess.Count == 0)
                    return Results.NotFound(new { error = "no booking rows found to cancel", bookingId, groupId });

                using var tx = await db.Database.BeginTransactionAsync();
                try
                {
                    var rowsResult = new List<object>();
                    decimal totalRefund = 0m;

                    foreach (var b in rowsToProcess)
                    {
                        if (b.Status == 3) // already cancelled
                        {
                            rowsResult.Add(new { bookingId = b.BookingId, skipped = true, reason = "already cancelled" });
                            continue;
                        }

                        // calculate refund per your rules (example: >=7 full, >=3 half, else 0)
                        decimal refund = 0m;
                        if (b.StartDate != default)
                        {
                            var daysUntil = (b.StartDate.Date - DateTime.UtcNow.Date).Days;
                            if (daysUntil >= 7) refund = b.TotalPrice;
                            else if (daysUntil >= 3) refund = Math.Round(b.TotalPrice * 0.5m, 2);
                            else refund = 0m;
                        }

                        // mark cancelled
                        b.Status = 3;
                        b.CancelReason = reason;
                        b.RefundAmount = refund;
                        db.Set<Booking>().Update(b);

                        totalRefund += refund;

                        int qty = Math.Max(1, b.Quantity);

                        var perNightChanges = new List<object>();
                        var start = b.StartDate;
                        var end = b.EndDate;
                        var roomId = b.RoomId;

                        if (!string.IsNullOrWhiteSpace(roomId) && start != default && end != default)
                        {
                            for (var dt = start.Date; dt < end.Date; dt = dt.AddDays(1))
                            {
                                try
                                {
                                    // find inventory row for that date
                                    var inventory = await db.Set<RoomInventory>().FirstOrDefaultAsync(ri => ri.RoomId == roomId && ri.Date == dt);

                                    // read room master for cap
                                    var roomMaster = await db.Set<Room>().FirstOrDefaultAsync(r => r.RoomId == roomId);
                                    int roomCount = (roomMaster?.RoomCount ?? 0);

                                    if (inventory == null)
                                    {
                                        // create if not exists, initial available = min(qty, roomCount>0?roomCount:qty)
                                        inventory = new RoomInventory
                                        {
                                            RoomId = roomId,
                                            Date = dt,
                                            AvailableCount = roomCount > 0 ? Math.Min(qty, roomCount) : qty
                                        };
                                        await db.AddAsync(inventory);
                                        await db.SaveChangesAsync(); // ensure tracked
                                    }

                                    var oldAvail = inventory.AvailableCount;
                                    var updated = oldAvail + qty;
                                    if (roomCount > 0 && updated > roomCount) updated = roomCount;

                                    inventory.AvailableCount = updated;
                                    db.Set<RoomInventory>().Update(inventory);

                                    // audit record
                                    var audit = new InventoryAudit
                                    {
                                        RoomId = roomId,
                                        Date = dt,
                                        OldAvailable = oldAvail,
                                        NewAvailable = updated,
                                        Delta = qty,
                                        Reason = $"Cancel: {reason}",
                                        BookingId = b.BookingId.ToString(),
                                        ChangedAt = DateTime.UtcNow
                                    };
                                    await db.AddAsync(audit);

                                    perNightChanges.Add(new { date = dt, oldAvailable = oldAvail, newAvailable = updated, delta = qty });
                                }
                                catch (Exception exDate)
                                {
                                    logger.LogWarning(exDate, "Failed to restore inventory for {Room} on {Date}", roomId, dt);
                                }
                            } // foreach date
                        }
                        else
                        {
                            // fallback: update master Room.AvailableCount
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(roomId))
                                {
                                    var rm = await db.Set<Room>().FirstOrDefaultAsync(r => r.RoomId == roomId);
                                    if (rm != null)
                                    {
                                        // normalize nullable RoomCount into an int
                                        int roomCount = rm.RoomCount ?? 0;

                                        var oldAvail = rm.AvailableCount;
                                        var updated = oldAvail + qty;
                                        if (roomCount > 0 && updated > roomCount) updated = roomCount;

                                        rm.AvailableCount = updated;
                                        db.Set<Room>().Update(rm);

                                        var audit = new InventoryAudit
                                        {
                                            RoomId = roomId,
                                            Date = null,
                                            OldAvailable = oldAvail,
                                            NewAvailable = updated,
                                            Delta = qty,
                                            Reason = $"Cancel (master): {reason}",
                                            BookingId = b.BookingId.ToString(),
                                            ChangedAt = DateTime.UtcNow
                                        };
                                        await db.AddAsync(audit);

                                        perNightChanges.Add(new { date = (DateTime?)null, oldAvailable = oldAvail, newAvailable = updated, delta = qty });
                                    }
                                }
                            }
                            catch (Exception exMaster)
                            {
                                logger.LogWarning(exMaster, "Fallback master room availability update failed for {Room}", roomId);
                            }
                        }

                        rowsResult.Add(new
                        {
                            bookingId = b.BookingId,
                            refundAmount = refund,
                            roomId = b.RoomId,
                            qtyRestored = qty,
                            perNight = perNightChanges
                        });
                    } // foreach rows

                    await db.SaveChangesAsync();
                    await tx.CommitAsync();

                    return Results.Ok(new { success = true, totalRefund, rows = rowsResult });
                }
                catch (Exception exTx)
                {
                    try { await tx.RollbackAsync(); } catch { }
                    logger.LogError(exTx, "Group cancel transaction failed for {BookingId}", bookingId);
                    return Results.Problem(title: "Server error while cancelling booking group", detail: exTx.ToString(), statusCode: 500);
                }
            }
            catch (Exception ex)
            {
                // fallback
                return Results.Problem(title: "Server error while cancelling booking", detail: ex.ToString(), statusCode: 500);
            }
        }).WithName("CancelBookingGroupPerNight");

        // ------------------------
        // SAVED (favorites)
        // ------------------------

        // GET saved list for user (enhanced: returns resolved Image URL, never 404 + extra apartment fields)
        app.MapGet("/api/saved/{userId:int}", async (int userId, AppDb db, HttpRequest req) =>
        {
            if (userId <= 0) return Results.BadRequest(new { error = "invalid userId" });

            string baseUrl = $"{req.Scheme}://{req.Host}/images/";

            var savedList = await db.SavedApartments
                .AsNoTracking()
                .Where(s => s.UserTestDataId == userId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new {
                    s.Id,
                    s.ApartmentId,
                    s.ApartmentTitle,
                    s.ApartmentImage,
                    s.CreatedAt
                })
                .ToListAsync();

            var result = new List<object>(savedList.Count);

            foreach (var s in savedList)
            {
                string imageUrl = null;

                // 1) denormalized ApartmentImage stored in Saved record
                if (!string.IsNullOrWhiteSpace(s.ApartmentImage))
                {
                    if (s.ApartmentImage.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        s.ApartmentImage.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        imageUrl = s.ApartmentImage;
                    }
                    else if (s.ApartmentImage.StartsWith("/"))
                    {
                        imageUrl = $"{req.Scheme}://{req.Host}{s.ApartmentImage}";
                    }
                    else
                    {
                        imageUrl = baseUrl + s.ApartmentImage;
                    }
                }

                // 2) if still not found, attempt to use ApartmentImages table (main image)
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    var mainImg = await db.ApartmentImages
                        .AsNoTracking()
                        .Where(i => i.ApartmentId == s.ApartmentId)
                        .OrderByDescending(i => i.IsMain)
                        .ThenBy(i => i.SortOrder)
                        .FirstOrDefaultAsync();

                    if (mainImg != null)
                    {
                        if (!string.IsNullOrWhiteSpace(mainImg.Url))
                        {
                            imageUrl = mainImg.Url;
                        }
                        else if (!string.IsNullOrWhiteSpace(mainImg.FileName))
                        {
                            if (mainImg.FileName.StartsWith("/"))
                                imageUrl = $"{req.Scheme}://{req.Host}{mainImg.FileName}";
                            else
                                imageUrl = baseUrl + mainImg.FileName;
                        }
                    }
                }

                // 3) next fallback: Apartments.DefaultImage
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    var aptA = await db.Apartments.AsNoTracking().FirstOrDefaultAsync(a => a.ApartmentId == s.ApartmentId);
                    if (aptA != null && !string.IsNullOrWhiteSpace(aptA.DefaultImage))
                    {
                        if (aptA.DefaultImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            imageUrl = aptA.DefaultImage;
                        else if (aptA.DefaultImage.StartsWith("/"))
                            imageUrl = $"{req.Scheme}://{req.Host}{aptA.DefaultImage}";
                        else
                            imageUrl = baseUrl + aptA.DefaultImage;
                    }
                }

                // final fallback
                if (string.IsNullOrWhiteSpace(imageUrl))
                    imageUrl = baseUrl + "default.jpg";

                // fetch extra apartment fields (Title, Description, Location, Rating)
                var apt = await db.Apartments
                    .AsNoTracking()
                    .Where(a => a.ApartmentId == s.ApartmentId)
                    .Select(a => new { a.Title, a.Description, a.Location, a.Rating })
                    .FirstOrDefaultAsync();

                // compute pricePerNight as the min PricePerNight among rooms (if any)
                decimal? pricePerNight = null;
                var anyRooms = await db.Rooms.AsNoTracking().Where(r => r.ApartmentId == s.ApartmentId && r.PricePerNight != null).ToListAsync();
                if (anyRooms != null && anyRooms.Count > 0)
                {
                    pricePerNight = anyRooms.Min(r => r.PricePerNight);
                }

                result.Add(new
                {
                    Id = s.Id,
                    ApartmentId = s.ApartmentId,
                    Title = string.IsNullOrWhiteSpace(s.ApartmentTitle) ? (apt?.Title ?? s.ApartmentId) : s.ApartmentTitle,
                    Image = imageUrl,
                    CreatedAt = s.CreatedAt,
                    Description = apt?.Description ?? string.Empty,
                    Location = apt?.Location ?? string.Empty,
                    PricePerNight = pricePerNight,      // nullable decimal (client can format)
                    Rating = apt?.Rating
                });
            }

            return Results.Ok(result);
        }).WithName("GetSavedWithImageAndMeta");

        app.MapPost("/api/saved", async (HttpContext httpCtx, AppDb db, [FromBody] SavedCreateReq req) =>
        {
            int? authUserId = null;
            var idClaim = httpCtx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
                          httpCtx.User?.FindFirst("sub")?.Value;
            if (int.TryParse(idClaim, out var parsed)) authUserId = parsed;

            var userId = authUserId ?? (req?.UserTestDataId > 0 ? req.UserTestDataId : 0);
            if (userId <= 0) return Results.BadRequest(new { error = "userId not provided and user not authenticated" });

            if (req == null || string.IsNullOrWhiteSpace(req.ApartmentId))
                return Results.BadRequest(new { error = "apartmentId required" });

            var existing = await db.SavedApartments.FirstOrDefaultAsync(s => s.UserTestDataId == userId && s.ApartmentId == req.ApartmentId);
            if (existing != null) return Results.Ok(new { success = true, savedId = existing.Id });

            var ent = new SavedApartmentEntity
            {
                UserTestDataId = userId,
                ApartmentId = req.ApartmentId,
                ApartmentTitle = req.ApartmentTitle,
                ApartmentImage = NormalizeImageForStorage(req.ApartmentImage),
                CreatedAt = DateTime.UtcNow
            };

            db.SavedApartments.Add(ent);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                System.Diagnostics.Debug.WriteLine("Save failed: " + ex);
                return Results.Problem("Could not save item", statusCode: 500);
            }

            return Results.Created($"/api/saved/{ent.Id}", new { success = true, savedId = ent.Id });
        }).WithName("AddSaved");

        app.MapDelete("/api/saved/{userId:int}/{apartmentId}", async (int userId, string apartmentId, HttpContext httpCtx, AppDb db) =>
        {
            int? authUserId = null;
            var idClaim = httpCtx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
                          httpCtx.User?.FindFirst("sub")?.Value;
            if (int.TryParse(idClaim, out var parsed)) authUserId = parsed;

            if (authUserId.HasValue && authUserId != userId) return Results.Forbid();

            if (userId <= 0 || string.IsNullOrWhiteSpace(apartmentId))
                return Results.BadRequest(new { error = "userId and apartmentId required" });

            var ent = await db.SavedApartments.FirstOrDefaultAsync(s => s.UserTestDataId == userId && s.ApartmentId == apartmentId);
            if (ent == null) return Results.NotFound(new { error = "saved item not found" });

            db.SavedApartments.Remove(ent);
            await db.SaveChangesAsync();

            return Results.Ok(new { success = true });
        }).WithName("RemoveSaved");

        app.MapPost("/api/saved/toggle", async (HttpContext httpCtx, AppDb db, [FromBody] SavedToggleReq req) =>
        {
            int? authUserId = null;
            var idClaim = httpCtx.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
                          httpCtx.User?.FindFirst("sub")?.Value;
            if (int.TryParse(idClaim, out var parsed)) authUserId = parsed;

            var userId = authUserId ?? (req?.UserTestDataId > 0 ? req.UserTestDataId : 0);
            if (userId <= 0) return Results.BadRequest(new { error = "userId not provided and user not authenticated" });

            if (req == null || string.IsNullOrWhiteSpace(req.ApartmentId)) return Results.BadRequest(new { error = "apartmentId required" });

            var existing = await db.SavedApartments.FirstOrDefaultAsync(s => s.UserTestDataId == userId && s.ApartmentId == req.ApartmentId);
            if (existing != null)
            {
                db.SavedApartments.Remove(existing);
                await db.SaveChangesAsync();
                return Results.Ok(new { success = true, action = "removed" });
            }

            var ent = new SavedApartmentEntity
            {
                UserTestDataId = userId,
                ApartmentId = req.ApartmentId,
                CreatedAt = DateTime.UtcNow
            };
            db.SavedApartments.Add(ent);
            await db.SaveChangesAsync();
            return Results.Ok(new { success = true, action = "added", savedId = ent.Id });
        }).WithName("ToggleSaved");



        // DEV: seed products data if empty (runs once at startup)

        app.MapGet("/dbg/products/test", async (AppDb db) =>
        {
            var ok = await db.Database.CanConnectAsync();
            var cats = await db.Categories.Take(5).ToListAsync();
            return Results.Ok(new { dbConnected = ok, categories = cats });
        });

        // This line expects an extension method MapProductsEndpoints(this WebApplication app, ...)
        app.MapProductsEndpoints();

// DEV: seed products data if empty (runs once at startup)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    try
    {
        await Ultivision.Modules.Products.ProductsDbSeeder.SeedAsync(db);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Products seeder failed: " + ex);
    }
}
        // ------------------------
        // Swagger + Run
        // ------------------------
        app.UseSwagger();
        app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "Ultivision API v1"); });

        await app.RunAsync();
    }
}


// =============================================================
// DTOs / Records and Models (declared after top-level startup code)
// =============================================================

record LoginReq(string Username, string Password);
record RegisterReq(string Username, string Name, string Password, string Email);
record ForgotReq(string Email);
record ResetReq(string Email, string Otp, string NewPassword);

record BookingCreateReq(
    string ApartmentId,
    string RoomId,
    DateTime StartDate,
    DateTime EndDate,
    int Guests,
    int Quantity,
    decimal TotalPrice,
    string ApartmentTitle,
    string ApartmentImage,
    string RoomName,
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    int? UserTestDataId
);

// DTO classes for saved endpoints (placed at file scope to avoid local-function/record issues)
public class SavedCreateReq
{
    public int UserTestDataId { get; set; }
    public string ApartmentId { get; set; } = string.Empty;
    public string? ApartmentTitle { get; set; }
    public string? ApartmentImage { get; set; }
}

public class SavedToggleReq
{
    public int UserTestDataId { get; set; }
    public string ApartmentId { get; set; } = string.Empty;
}

// ---------------- MULTI-BOOKING DTOs ----------------

public class BookingRoomRequest
{
    public string RoomId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal PricePerNight { get; set; }
}

public class MultiBookingCreateReq
{
    public int? UserTestDataId { get; set; }
    public string ApartmentId { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public int Adults { get; set; }
    public int Children { get; set; }

    public List<BookingRoomRequest> Rooms { get; set; } = new();
    public decimal TotalPrice { get; set; }

    // Customer fields
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }

    // Optional denormalized apartment fields (used to save display-friendly title/image)
    public string? ApartmentTitle { get; set; }
    public string? ApartmentImage { get; set; }
}

// ---------------- MULTI-BOOKING GROUP MODEL ----------------
[Table("BookingGroups")]
public class BookingGroup
{
    [Key]
    public Guid BookingGroupId { get; set; } = Guid.NewGuid();

    public string? ApartmentId { get; set; }
    public int? UserTestDataId { get; set; }
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "SAR";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}


// =============================================================
// DbContext
// =============================================================
public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> opts) : base(opts) { }

    public DbSet<UserTestData> UserTestData => Set<UserTestData>();
    public DbSet<Apartment> Apartments => Set<Apartment>();
    public DbSet<ApartmentImage> ApartmentImages => Set<ApartmentImage>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomAvailability> RoomAvailability => Set<RoomAvailability>();
    // Per-night canonical inventory table (added)
    public DbSet<RoomInventory> RoomInventory => Set<RoomInventory>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<SavedApartmentEntity> SavedApartments => Set<SavedApartmentEntity>();
    // Products module DbSets
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierInventory> SupplierInventory => Set<SupplierInventory>();

    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
	public DbSet<OrderStatusLookup> OrderStatusLookup => Set<OrderStatusLookup>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();
    public DbSet<BookingGroup> BookingGroups => Set<BookingGroup>();

    // Audit table for inventory changes
    public DbSet<InventoryAudit> InventoryAudits => Set<InventoryAudit>();
	
	protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // ✅ FIX for reserved keyword table: dbo.Order
    modelBuilder.Entity<Order>()
        .ToTable("Order", "dbo");

    base.OnModelCreating(modelBuilder);
}
}

// =============================================================
// Models (nullable reference types where DB can have NULLs)
// =============================================================
[Table("UserTestData")]
public class UserTestData
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("username")] public string? Username { get; set; }
    [Column("plain_password")] public string? PlainPassword { get; set; }
    [Column("password_hash")] public string? PasswordHash { get; set; }
    [Column("display_name")] public string? DisplayName { get; set; }
    [Column("email")] public string? Email { get; set; }
    [Column("otp")] public string? Otp { get; set; }

    [Column("failed_login_count")] public int FailedLoginCount { get; set; }
    [Column("success_login_count")] public int SuccessLoginCount { get; set; }
    [Column("failed_login_time")] public DateTime? FailedLoginTime { get; set; }
    [Column("success_login_time")] public DateTime? SuccessLoginTime { get; set; }
    [Column("ip_address")] public string? IPAddress { get; set; }

    [Column("customer_type")] public string? CustomerType { get; set; }
}

[Table("Apartments")]
public class Apartment
{
    [Key]
    [Column("ApartmentId")]
    public string ApartmentId { get; set; } = string.Empty;

    [Column("Title")] public string? Title { get; set; }
    [Column("Subtitle")] public string? Subtitle { get; set; }
    [Column("Description")] public string? Description { get; set; }
    [Column("Location")] public string? Location { get; set; }
    [Column("Address")] public string? Address { get; set; }
    [Column("Latitude")] public decimal? Latitude { get; set; }
    [Column("Longitude")] public decimal? Longitude { get; set; }
    [Column("DefaultImage")] public string? DefaultImage { get; set; }
    [Column("Rating")] public decimal? Rating { get; set; }
    [Column("Amenities")] public string? Amenities { get; set; }
}

[Table("ApartmentImages")]
public class ApartmentImage
{
    [Key]
    [Column("ImageId")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int ImageId { get; set; }

    [Column("ApartmentId")] public string? ApartmentId { get; set; }
    [Column("FileName")] public string? FileName { get; set; }           // e.g. "jed09.jpg"
    [Column("Url")] public string? Url { get; set; }                     // optional full URL
    [Column("IsMain")] public bool IsMain { get; set; } = false;        // mark primary image
    [Column("SortOrder")] public int SortOrder { get; set; } = 0;
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("Rooms")]
public class Room
{
    [Key]
    [Column("RoomId")]
    public string RoomId { get; set; } = string.Empty;

    [Column("ApartmentId")] public string? ApartmentId { get; set; }
    [Column("Name")] public string? Name { get; set; }
    [Column("BedInfo")] public string? BedInfo { get; set; }
    [Column("MaxPeople")] public int? MaxPeople { get; set; }
    [Column("PricePerNight")] public decimal? PricePerNight { get; set; }
    [Column("IsInstantBook")] public bool? IsInstantBook { get; set; }
    [Column("ShortAmenities")] public string? ShortAmenities { get; set; }
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // NEW: number of identical rooms available for this RoomId (matches DB column RoomCount)
    [Column("RoomCount")] public int? RoomCount { get; set; } = 1;

    // master-level available count (optional column in some schemas)
    [Column("AvailableCount")] public int AvailableCount { get; set; } = 0;
}

[Table("RoomAvailability")]
public class RoomAvailability
{
    [Key]
    [Column("Id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("RoomId")] public string? RoomId { get; set; }
    [Column("StartDate")] public DateTime StartDate { get; set; }
    [Column("EndDate")] public DateTime EndDate { get; set; }
    [Column("IsBlocked")] public bool IsBlocked { get; set; } = true;
    // NEW: how many units are blocked in this row
    [Column("Quantity")] public int Quantity { get; set; } = 1;

    [Column("Note")] public string? Note { get; set; }
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Per-night inventory table (tracks available count for a room on a specific date)
[Table("RoomInventory")]
public class RoomInventory
{
    [Key]
    [Column("Id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("RoomId")] public string? RoomId { get; set; }
    [Column("Date")] public DateTime Date { get; set; }
    [Column("AvailableCount")] public int AvailableCount { get; set; } = 0;
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Audit record for inventory changes (persists what changed and why)
[Table("InventoryAudit")]
public class InventoryAudit
{
    [Key]
    [Column("Id")]
    public int Id { get; set; }

    [Column("RoomId")] public string? RoomId { get; set; }
    [Column("Date")] public DateTime? Date { get; set; } // null for master-level changes
    [Column("OldAvailable")] public int OldAvailable { get; set; }
    [Column("NewAvailable")] public int NewAvailable { get; set; }
    [Column("Delta")] public int Delta { get; set; }
    [Column("Reason")] public string? Reason { get; set; }
    [Column("BookingId")] public string? BookingId { get; set; }
    [Column("ChangedAt")] public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}


[Table("Customers")]
public class Customer
{
    [Key]
    [Column("CustomerId")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int CustomerId { get; set; }

    [Column("ExternalUserId")] public string? ExternalUserId { get; set; }
    [Column("FullName")] public string? FullName { get; set; }
    [Column("Email")] public string? Email { get; set; }
    [Column("Phone")] public string? Phone { get; set; }
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("Bookings")]
public class Booking
{
    [Key]
    [Column("BookingId")]
    public Guid BookingId { get; set; } = Guid.NewGuid();

    [Column("ApartmentId")] public string? ApartmentId { get; set; }
    [Column("RoomId")] public string? RoomId { get; set; }
    [Column("CustomerId")] public int? CustomerId { get; set; }
    [Column("UserTestDataId")] public int? UserTestDataId { get; set; }
    [Column("StartDate")] public DateTime StartDate { get; set; }
    [Column("EndDate")] public DateTime EndDate { get; set; }
    [Column("Guests")] public int Guests { get; set; } = 1;
    [Column("TotalPrice")] public decimal TotalPrice { get; set; }
    [Column("Quantity")] public int Quantity { get; set; } = 1;
    [Column("Currency")] public string? Currency { get; set; } = "SAR";
    [Column("Status")] public int Status { get; set; } = 0;
    [Column("CancelReason")] public string? CancelReason { get; set; }
    [Column("RefundAmount")] public decimal? RefundAmount { get; set; }
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("ApartmentTitle")] public string? ApartmentTitle { get; set; }
    [Column("ApartmentImage")] public string? ApartmentImage { get; set; }
    [Column("RoomName")] public string? RoomName { get; set; }

    [Column("CustomerName")] public string? CustomerName { get; set; }
    [Column("CustomerEmail")] public string? CustomerEmail { get; set; }
    [Column("CustomerPhone")] public string? CustomerPhone { get; set; }
    [Column("BookingGroupId")] public Guid? BookingGroupId { get; set; }


    // navigation properties (optional)
    public Customer? Customer { get; set; }
    public UserTestData? UserTestData { get; set; }
}

[Table("Payments")]
public class Payment
{
    [Key]
    [Column("PaymentId")]
    public Guid PaymentId { get; set; } = Guid.NewGuid();

    [Column("BookingId")] public Guid BookingId { get; set; }
    [Column("Amount")] public decimal Amount { get; set; }
    [Column("Currency")] public string? Currency { get; set; } = "SAR";
    [Column("Method")] public string? Method { get; set; }
    [Column("TransactionId")] public string? TransactionId { get; set; }
    [Column("Status")] public string? Status { get; set; }
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("SavedApartments")]
public class SavedApartmentEntity
{
    [Key]
    [Column("Id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    // FK to UserTestData.id
    [Column("UserTestDataId")]
    public int UserTestDataId { get; set; }

    [Column("ApartmentId")]
    public string ApartmentId { get; set; } = string.Empty;

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("ApartmentTitle")] public string? ApartmentTitle { get; set; }
    [Column("ApartmentImage")] public string? ApartmentImage { get; set; }
}
