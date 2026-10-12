using Newtonsoft.Json;

namespace Contoso.Catalog
{
    /// <summary>An object-typed payload: the concrete type comes from the callers.</summary>
    public static class Envelope
    {
        public static string Wrap(object payload) => JsonConvert.SerializeObject(payload);
    }

    public static class ProductFeed
    {
        public static string Publish(Product product) => Envelope.Wrap(product);
    }
}
