using System;
using Evidence.Client;
using Evidence.Engine;
using Evidence.Shop.Controllers;
using Evidence.Tools;

namespace Evidence.Shop
{
    internal static class Program
    {
        private static void Main()
        {
            Console.WriteLine(new ShopClient().Endpoint);
            Console.WriteLine(Checksum.Of("order"));
            Bootstrapper.Run(new AppDomainTypeFinder());

            // A route to an action, spelled in another letter case: MVC matches action names without regard to case.
            Console.WriteLine(typeof(BoardsController).Name + "/" + "ActiveDiscussionsRSS");
        }
    }
}
