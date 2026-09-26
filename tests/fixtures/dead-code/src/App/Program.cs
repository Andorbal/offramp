using System;
using DeadCode.Contracts;
using DeadCode.Core;

namespace DeadCode.App
{
    internal static class Program
    {
        private static void Main()
        {
            var customer = new CustomerDto { Name = "Contoso" };
            Console.WriteLine(customer.Name + ": " + new OrderService().Total(100m, 0.2m));

            var plugin = Type.GetType("DeadCode.Core.ReportPlugin, Core");
            Console.WriteLine(plugin);

            new Registry().Scan(typeof(IHandler).Assembly);
        }
    }
}
