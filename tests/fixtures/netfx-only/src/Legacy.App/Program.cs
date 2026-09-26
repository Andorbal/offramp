using System;
using Legacy.Core;
using Newtonsoft.Json;

namespace Legacy.App
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            var encoded = UrlHelper.Encode(args.Length > 0 ? args[0] : "hello world");
            Console.WriteLine(JsonConvert.SerializeObject(new { encoded }));
        }
    }
}
