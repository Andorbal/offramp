using Contoso.Orders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Contoso.Orders.Tests
{
    [TestClass]
    public class OrderCodecTests
    {
        [TestMethod]
        public void Round_trips_the_customer()
        {
            var json = OrderCodec.Write(new Order { OrderId = 7, CustomerName = "Ada" });

            Assert.AreEqual("Ada", OrderCodec.Read(json).CustomerName);
        }
    }
}
