using System.Web;

namespace Site.Admin
{
    public static class AdminArea
    {
        public static string Title(HttpContextBase context) => Core.Greeting.For("admin") + " " + context.Request.Path;
    }
}
