namespace Ultivision.Modules.Products.Dtos
{
    public class AddressCreateUpdateDto
    {
        public string? Title { get; set; }
        public string FullAddress { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; }
        public string? Phone { get; set; }
        public bool IsDefault { get; set; }
    }
}
