namespace Ultivision.Modules.Products.Dtos
{
    public class PaymentMethodCreateUpdateDto
    {
        /// <summary>
        /// Display label shown to the user
        /// Example: "Visa •••• 4242"
        /// </summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Provider type
        /// Example: Card, UPI, Wallet, NetBanking
        /// </summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// Token or reference returned by payment gateway
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Marks this payment method as default
        /// </summary>
        public bool IsDefault { get; set; }
    }
}
