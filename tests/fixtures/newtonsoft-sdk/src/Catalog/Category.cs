using System.Collections.Generic;
using Newtonsoft.Json;

namespace Contoso.Catalog
{
    /// <summary>A type that contains itself, read without an explicit MaxDepth.</summary>
    public class Category
    {
        public string Name { get; set; }
        public List<Category> Children { get; set; }
    }

    public static class CategoryCodec
    {
        public static Category Read(string json) => JsonConvert.DeserializeObject<Category>(json);
    }
}
