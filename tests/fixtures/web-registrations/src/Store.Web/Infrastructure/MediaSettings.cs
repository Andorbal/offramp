using System.Configuration;

namespace Store.Web.Infrastructure
{
    public static class MediaSettings
    {
        public static string PublicPath()
        {
            return ConfigurationManager.AppSettings["MediaPath"];
        }

        public static string Tenant()
        {
            return "default";
        }
    }
}
