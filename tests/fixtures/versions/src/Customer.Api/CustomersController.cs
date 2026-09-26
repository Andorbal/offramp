namespace Customer.Api
{
    public class CustomersController
    {
        public string Describe(object customer) => Newtonsoft.Json.JsonConvert.SerializeObject(customer);

        public string Encode(string value) => System.Web.HttpUtility.HtmlEncode(value);
    }
}
