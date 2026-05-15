// File: Modules/Products/Models.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ultivision.Modules.Products
{
    public enum OrderStatus
    {
        Processing = 0,
        SupplierPickup = 1,
        OutForDelivery = 2,
        Delivered = 3,
        ReturnRequested = 4,
        Returned = 5,
        Cancelled = 6
    }

    [Table("Category")]
    public class Category
    {
        [Key] public int CategoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }           // you asked for this
        public string? ImageUrl { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    [Table("Product")]
    public class Product
    {
        [Key] public int ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }          // you asked for this
        public int? CategoryId { get; set; }
        public string? ImageUrl { get; set; }
        public decimal BasePrice { get; set; } = 0m;
        public int Stock { get; set; } = 0;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    [Table("Supplier")]
    public class Supplier
    {
        [Key] public int SupplierId { get; set; }
        public int ProductId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; } = 0m;
        public string? ContactInfo { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // server-side model
    [Table("SupplierInventory")]
    public class SupplierInventory
    {
        [Key]
        [Column("SupplierInventoryId")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SupplierInventoryId { get; set; }

        [Column("SupplierId")] public int SupplierId { get; set; }
        [Column("ProductId")] public int ProductId { get; set; }
        [Column("CategoryId")] public int? CategoryId { get; set; }
        [Column("QuantityAvailable")] public int QuantityAvailable { get; set; } = 0;
        [Column("ReorderLevel")] public int? ReorderLevel { get; set; }
        [Column("SupplierPrice")] public decimal? SupplierPrice { get; set; }
        [Column("LeadTimeDays")] public int? LeadTimeDays { get; set; }
        [Column("LastUpdated")] public DateTime? LastUpdated { get; set; }
        [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Column("IsActive")] public bool IsActive { get; set; } = true;
    }

    [Table("CartItem")]
    public class CartItem
    {
        [Key] public int CartItemId { get; set; }
        public string? UserId { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int? SupplierId { get; set; }
        public string? SupplierName { get; set; }
        public int Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; } = 0m;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

[Table("Order", Schema = "dbo")]
public class Order
{
    [Key]
    public int OrderId { get; set; }

    [Required]
    [MaxLength(50)]
    public string OrderNumber { get; set; } = null!;

    // JWT user id → nvarchar(450)
    public string? UserId { get; set; }

    public int? AddressId { get; set; }

    public int? PaymentMethodId { get; set; }

    // stored as INT in DB
    public OrderStatus Status { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}

[Table("OrderStatusLookup")]
public class OrderStatusLookup
{
    [Key]
    public int StatusCode { get; set; }
    public string StatusName { get; set; } = string.Empty;
}

    [Table("OrderItem")]
    public class OrderItem
    {
        [Key] public int OrderItemId { get; set; }
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int? SupplierId { get; set; }
        public string? SupplierName { get; set; }
        public int Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; } = 0m;
		 [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public decimal LineTotal { get; private set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    [Table("Address")]
    public class Address
    {
        [Key]
        [Column("AddressId")]
        public int AddressId { get; set; }

        [Column("UserId")]
        public string? UserId { get; set; } = string.Empty;

        [Column("Title")]
        public string? Title { get; set; }

        [Column("FullAddress")]
        public string? FullAddress { get; set; }

        [Column("City")]
        public string? City { get; set; }

        [Column("State")]
        public string? State { get; set; }

        [Column("PostalCode")]
        public string? PostalCode { get; set; }

        [Column("Country")]
        public string? Country { get; set; }

        [Column("Phone")]
        public string? Phone { get; set; }

        [Column("IsDefault")]
        public bool IsDefault { get; set; }

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("UpdatedAt")]
        public DateTime? UpdatedAt { get; set; }

        // client-friendly display helpers (not stored)
        [NotMapped]
        public string ShortAddress => $"{Title}: {FullAddress}, {City}, {Country}";

        [NotMapped]
        public string DisplayText => $"{Title} — {FullAddress}, {City} {PostalCode}";
    }

    [Table("PaymentMethod")]
    public class PaymentMethod
    {
        [Key]
        [Column("PaymentMethodId")]
        public int PaymentMethodId { get; set; }

        [Column("UserId")]
        public string? UserId { get; set; } = string.Empty;

        [Column("Label")]
        public string? Label { get; set; } // e.g., "Visa **** 4242"

        [Column("Provider")]
        public string? Provider { get; set; } // e.g., "Card"

        [Column("Token")]
        public string? Token { get; set; }

        [Column("IsDefault")]
        public bool IsDefault { get; set; }

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("UpdatedAt")]
        public DateTime? UpdatedAt { get; set; }

        [NotMapped]
        public string DisplayText => $"{Label} ({Provider})";

        [NotMapped]
        public string DisplayName => DisplayText;
    }

    [Table("ReturnRequest")]
    public class ReturnRequest
    {
        [Key] public int ReturnRequestId { get; set; }
        public int OrderId { get; set; }
        public string? Reason { get; set; }
        public string Status { get; set; } = "Requested";
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    }
}
