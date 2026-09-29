using System;
using Foo.Service;

namespace Foo.TestData
{
    /// <summary>Used only by tests. Internal: public types of a library other repositories may use never move (ADR 0045).</summary>
    internal sealed class OrderBuilder
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
