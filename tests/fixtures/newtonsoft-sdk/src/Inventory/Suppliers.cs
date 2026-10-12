using System.Collections.Generic;
using Contoso.Catalog;
using Newtonsoft.Json;

namespace Contoso.Inventory
{
    /// <summary>Only ever read: a probe needs a real payload.</summary>
    public class Supplier
    {
        public string Name { get; set; }
        public decimal MinimumOrder { get; set; }
    }

    public static class SupplierFeed
    {
        public static List<Supplier> Parse(string json) => JsonConvert.DeserializeObject<List<Supplier>>(json);
    }

    /// <summary>Internal, and no friend assembly: not reachable from a test.</summary>
    internal static class Reorder
    {
        internal static string Plan(Product product, int quantity) =>
            JsonConvert.SerializeObject(new Dictionary<string, object> { ["sku"] = product.SKUCode, ["quantity"] = quantity });
    }
}
