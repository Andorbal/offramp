using System;
using System.Diagnostics;
using System.Text;

namespace Shop
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            var latin = Encoding.GetEncoding(1252);
            Console.WriteLine(latin.WebName);
            if (args.Length > 0)
            {
                OpenHelp(args[0]);
            }

            return 0;
        }

        private static void OpenHelp(string url)
        {
            Process.Start(url);
        }
    }
}
