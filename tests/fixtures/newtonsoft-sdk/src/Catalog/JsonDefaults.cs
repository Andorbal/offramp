using Newtonsoft.Json;

namespace Contoso.Catalog
{
    /// <summary>Process-wide default settings, which probes cannot reproduce.</summary>
    public static class JsonDefaults
    {
        public static void Install()
        {
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Utc };
        }
    }
}
