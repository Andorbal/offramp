using System;
using Contoso.Orders;

namespace Contoso.Orders.App
{
    internal static class Program
    {
        private static void Main()
        {
            Console.WriteLine(OrderCodec.Write(new Order { OrderId = 1, CustomerName = "Ada" }));
        }
    }
}
