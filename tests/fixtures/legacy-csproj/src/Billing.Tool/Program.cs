using System;
using System.Configuration;
using Contoso.Billing;

namespace Contoso.Billing.Tool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var billing = BillingSection.Current;
            var invoice = new Invoice { Number = ConfigurationManager.AppSettings["InvoicePrefix"] + "-1", Total = 12.5m };
            Console.WriteLine(Invoice.Title() + " " + invoice.ToJson() + " " + billing.Currency + " v" + VersionInfo.Text);
            Console.WriteLine(ConfigurationManager.ConnectionStrings["Billing"].ConnectionString);
            return 0;
        }
    }
}
