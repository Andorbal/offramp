using System;
using Newtonsoft.Json.Linq;

namespace Contoso.Catalog
{
    /// <summary>Relies on the exception a mismatched token conversion throws.</summary>
    public static class PriceReader
    {
        public static decimal? TryRead(string json)
        {
            var token = JObject.Parse(json)["price"];
            try
            {
                return token.ToObject<decimal>();
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }
    }
}
