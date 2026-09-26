using System;
using Accounts.Auth;
using Accounts.Directory;
using Accounts.Reports;
using Accounts.Users;

namespace Portal
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            var users = new UserService(new DirectoryLookup());
            Console.WriteLine(users.DisplayName(args.Length > 0 ? args[0] : "jdoe"));
            Console.WriteLine(new ReportService().Header(new DirectoryEntryInfo { DisplayName = "J. Doe" }));
            Console.WriteLine(new LoginHandler().CanLogIn("jdoe"));
        }
    }
}
