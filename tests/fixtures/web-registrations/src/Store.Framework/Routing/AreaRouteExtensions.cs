using System.Web.Mvc;
using System.Web.Routing;

namespace Store.Framework.Routing
{
    public static class AreaRouteExtensions
    {
        /// <summary>A route of an area without an AreaRegistration class: the area is a data token.</summary>
        public static Route MapAreaRoute(this RouteCollection routes, string area, string name, string url, object defaults)
        {
            var route = routes.MapRoute(name, url, defaults);
            route.DataTokens["area"] = area;
            return route;
        }
    }
}
