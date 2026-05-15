public class AddToCartDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}