using System.Web.Routing;

namespace Store.Framework.Routing
{
    /// <summary>A route that also matches the URL with a language prefix (/de/cart).</summary>
    public class LocalizedRoute : Route
    {
        public LocalizedRoute(string url, IRouteHandler routeHandler)
            : base(url, routeHandler)
        {
        }
    }
}
