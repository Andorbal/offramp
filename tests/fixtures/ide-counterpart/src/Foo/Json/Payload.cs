using Newtonsoft.Json;

namespace Foo.Json
{
    /// <summary>Uses Newtonsoft.Json, which ModernF does not reference: more than a move (OFR6003).</summary>
    public static class Payload
    {
        public static string Write(object value) => JsonConvert.SerializeObject(value);
    }
}
