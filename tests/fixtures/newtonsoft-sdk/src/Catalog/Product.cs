using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace Contoso.Catalog
{
    public enum Color
    {
        [EnumMember(Value = "red")] Red,
        [EnumMember(Value = "dark-blue")] DarkBlue,
    }

    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string SKUCode { get; set; }
        public decimal Price { get; set; }
        public DateTime Created { get; set; }
        public Uri Link { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<Color, int> Stock { get; set; }

        [JsonProperty("LEGACY CODE")]
        public string LegacyCode { get; set; }

        public Dictionary<string, string> Attributes { get; set; }
    }
}
