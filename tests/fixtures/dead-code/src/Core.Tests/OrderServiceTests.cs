using System;
using DeadCode.Core;

namespace DeadCode.Core.Tests
{
    public sealed class OrderServiceTests
    {
        public void Total_rounds_to_cents()
        {
            var clock = new FixedClock(new DateTime(2026, 1, 1));
            if (new OrderService().Total(10m, 0.075m) != 10.75m || clock.Now().Year != 2026)
            {
                throw new InvalidOperationException("wrong total");
            }
        }
    }
}
