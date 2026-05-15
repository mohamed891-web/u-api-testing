using System;
using System.Collections.Generic;

namespace Ultivision.Modules.Products.Dtos
{
    public class OrderWithItemsDto
    {
        public int OrderId { get; set; }
        public string OrderNumber { get; set; } = string.Empty;

        public int StatusCode { get; set; }
        public string StatusName { get; set; } = string.Empty;

        public decimal TotalAmount { get; set; }
        public DateTime CreatedAt { get; set; }

        public List<OrderItemDto> Items { get; set; } = new();
    }

    public class OrderItemDto
    {
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }
}
