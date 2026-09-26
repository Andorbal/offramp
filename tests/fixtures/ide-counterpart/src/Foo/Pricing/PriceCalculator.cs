using System;
using System.Collections.Generic;
using System.Linq;

namespace Foo.Pricing
{
    /// <summary>Needs nothing but the base class library: can live in ModernF as it is.</summary>
    public sealed class PriceCalculator
    {
        public decimal Total(IEnumerable<decimal> prices, decimal discount) =>
            Math.Max(0m, prices.Sum() - discount);
    }
}
