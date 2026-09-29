using System;
using Evidence.Client;
using Evidence.Tools;

namespace Evidence.Shop
{
    internal static class Program
    {
        private static void Main()
        {
            Console.WriteLine(new ShopClient().Endpoint);
            Console.WriteLine(Checksum.Of("order"));
        }
    }
}
