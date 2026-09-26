using Newtonsoft.Json;

namespace Contoso.Billing
{
    public class Invoice
    {
        public string Number { get; set; }

        public decimal Total { get; set; }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }

        public static string Title()
        {
            return Strings.InvoiceTitle;
        }
    }
}
