using System.Web;

namespace Legacy.Core
{
    public static class UrlHelper
    {
        public static string Encode(string value)
        {
            return HttpUtility.UrlEncode(value);
        }

        public static string CurrentPath()
        {
            return HttpContext.Current == null ? null : HttpContext.Current.Request.Path;
        }
    }
}
