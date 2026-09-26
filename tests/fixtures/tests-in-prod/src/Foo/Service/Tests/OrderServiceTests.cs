using Foo.Shared;
using Foo.TestData;
using Xunit;

namespace Foo.Service.Tests
{
    public class OrderServiceTests
    {
        [Fact]
        public void New_orders_get_no_discount()
        {
            var service = new OrderService(new SystemClock());
            var order = new OrderBuilder().PlacedDaysAgo(1).WithAmount(100m).Build();

            Assert.Equal(0m, service.Discount(order));
            Assert.Equal(100m, service.Total(order));
        }

        [Fact]
        public void Old_orders_get_ten_percent()
        {
            var service = new OrderService(new SystemClock());
            var order = new OrderBuilder().PlacedDaysAgo(45).WithAmount(100m).Build();

            Assert.Equal(90m, service.Total(order));
        }
    }
}
