using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Contoso.Catalog
{
    /// <summary>Settings in a static member; the sites are their own entry points.</summary>
    public static class CatalogCodec
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = { new StringEnumConverter { AllowIntegerValues = false } },
        };

        public static string Write(Product product) => JsonConvert.SerializeObject(product, Settings);

        public static Product Read(string json) => JsonConvert.DeserializeObject<Product>(json, Settings);
    }
}
