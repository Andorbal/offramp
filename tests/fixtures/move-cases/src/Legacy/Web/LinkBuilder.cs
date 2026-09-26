using System.Web;

namespace Legacy.Web
{
    /// <summary>(e) Uses HttpContext, which exists only in System.Web on .NET Framework.</summary>
    public static class LinkBuilder
    {
        public static string Current() => HttpContext.Current?.Request.RawUrl ?? "/";
    }
}
