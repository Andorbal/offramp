using Foo.Pricing;

namespace Foo.Orders
{
    /// <summary>Uses PriceCalculator, which stays in Foo: needs a co-move (OFR2101).</summary>
    public sealed class OrderService
    {
        private readonly PriceCalculator _calculator = new PriceCalculator();

        public decimal Checkout(decimal[] prices) => _calculator.Total(prices, 0m);
    }
}
