using System.Web;

namespace Foo.Web
{
    /// <summary>Uses System.Web: does not compile in ModernF (OFR2103).</summary>
    public static class CookieReader
    {
        public static string Read(string name) => HttpContext.Current?.Request.Cookies[name]?.Value;
    }
}
