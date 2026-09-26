using System;
using Foo.Service;

namespace Foo.TestData
{
    /// <summary>Used only by tests.</summary>
    public sealed class OrderBuilder
    {
        private decimal _amount = 10m;
        private int _daysAgo;

        public OrderBuilder WithAmount(decimal amount)
        {
            _amount = amount;
            return this;
        }

        public OrderBuilder PlacedDaysAgo(int days)
        {
            _daysAgo = days;
            return this;
        }

        public Order Build() => new Order(_amount, DateTime.Today.AddDays(-_daysAgo));
    }
}
