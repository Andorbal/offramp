using System;
using Foo.Shared;

namespace Foo.Service
{
    public sealed class Order
    {
        public Order(decimal amount, DateTime placed)
        {
            Amount = amount;
            Placed = placed;
        }

        public decimal Amount { get; }

        public DateTime Placed { get; }
    }

    public sealed class OrderService
    {
        private readonly IClock _clock;

        public OrderService(IClock clock) => _clock = clock;

        public decimal Total(Order order) => order.Amount - Discount(order);

        /// <summary>Internal, and exercised directly by the tests.</summary>
        internal decimal Discount(Order order) =>
            (_clock.Today - order.Placed).TotalDays > 30 ? order.Amount * 0.1m : 0m;
    }
}
