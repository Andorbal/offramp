using System;
using System.Collections.Generic;

namespace Contoso.Orders
{
    public enum OrderStatus
    {
        Placed,
        Shipped,
        Cancelled,
    }

    public class Order
    {
        public int OrderId { get; set; }
        public string CustomerName { get; set; }
        public decimal Total { get; set; }
        public DateTime PlacedAt { get; set; }
        public OrderStatus Status { get; set; }
        public List<OrderLine> Lines { get; set; }
    }

    public class OrderLine
    {
        public string Sku { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }
}
