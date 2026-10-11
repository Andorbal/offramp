using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Contoso.Orders
{
    public static class OrderCodec
    {
        // TypeNameAssemblyFormat is obsolete from 10.0.1.
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple,
        };

        public static string Write(Order order)
        {
            return JsonConvert.SerializeObject(order, Settings);
        }

        public static Order Read(string json)
        {
            return JsonConvert.DeserializeObject<Order>(json, Settings);
        }
    }
}
