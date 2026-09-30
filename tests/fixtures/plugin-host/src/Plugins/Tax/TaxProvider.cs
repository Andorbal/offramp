using System.Web;
using Newtonsoft.Json;

namespace Plugins.Tax
{
    public class TaxProvider
    {
        public string Describe(HttpContextBase context) =>
            JsonConvert.SerializeObject(new { rate = 0.2m, greeting = Core.Greeting.For(context.Request.Path) });
    }
}
