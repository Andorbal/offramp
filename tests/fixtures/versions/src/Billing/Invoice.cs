using System.Configuration;

namespace Billing
{
    public class Invoice
    {
        public string Currency => ConfigurationManager.AppSettings["currency"] ?? "EUR";

        public string ToJson() => Newtonsoft.Json.JsonConvert.SerializeObject(this);
    }
}
