using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Contoso.Catalog
{
    /// <summary>Inline settings in a private helper; the entry point is one caller up.</summary>
    public sealed class ProductExporter
    {
        public string Export(IEnumerable<Product> products)
        {
            var lines = new StringBuilder();
            foreach (var product in products)
            {
                lines.Append(Line(product)).Append('\n');
            }

            return lines.ToString();
        }

        private static string Line(Product product) =>
            JsonConvert.SerializeObject(product, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    }
}
