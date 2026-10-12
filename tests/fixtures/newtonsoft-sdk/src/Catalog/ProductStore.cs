using System.IO;
using Newtonsoft.Json;

namespace Contoso.Catalog
{
    /// <summary>Writes a file: not an entry point a probe can call.</summary>
    public sealed class ProductStore
    {
        private readonly string _folder;

        public ProductStore(string folder)
        {
            _folder = folder;
        }

        public void Save(Product product)
        {
            File.WriteAllText(Path.Combine(_folder, product.Id + ".json"), JsonConvert.SerializeObject(product));
        }
    }
}
