using Contracts;
using Legacy.Clean;

namespace Legacy.Orders
{
    /// <summary>(c) Needs the Contracts project, which Core does not reference yet.</summary>
    public static class OrderMapper
    {
        public static OrderDto ToDto(string id, Money total) => new OrderDto { Id = id, Total = total.Amount };
    }
}
