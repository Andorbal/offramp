using Newtonsoft.Json;

namespace Legacy.Json
{
    /// <summary>(b) Needs Newtonsoft.Json, which Core does not reference yet.</summary>
    public static class Serializer
    {
        public static string ToJson(object value) => JsonConvert.SerializeObject(value);
    }
}
