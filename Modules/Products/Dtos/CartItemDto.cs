public class CartItemDto
{
    public int CartItemId { get; set; }

    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string ImageUrl { get; set; } = "";

    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = "";

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}