using System.Web.Mvc;
using System.Web.Routing;

namespace Store.Framework.Routing
{
    /// <summary>
    /// The codebase's own way to declare routes (SmartStoreNET's and nopCommerce's pattern):
    /// overloads that end in one method that constructs the route and adds it.
    /// </summary>
    public static class LocalizedRouteExtensions
    {
        public static Route MapLocalizedRoute(this RouteCollection routes, string name, string url)
        {
            return MapLocalizedRoute(routes, name, url, null /* defaults */, (object)null /* constraints */);
        }

        public static Route MapLocalizedRoute(this RouteCollection routes, string name, string url, object defaults)
        {
            return MapLocalizedRoute(routes, name, url, defaults, (object)null /* constraints */);
        }

        public static Route MapLocalizedRoute(this RouteCollection routes, string name, string url, object defaults, object constraints)
        {
            return MapLocalizedRoute(routes, name, url, defaults, constraints, null /* namespaces */);
        }

        public static Route MapLocalizedRoute(this RouteCollection routes, string name, string url, object defaults, string[] namespaces)
        {
            return MapLocalizedRoute(routes, name, url, defaults, null /* constraints */, namespaces);
        }

        public static Route MapLocalizedRoute(this RouteCollection routes, string name, string url, object defaults, object constraints, string[] namespaces)
        {
            return MapLocalizedRouteInternal(routes, name, url, defaults, constraints, namespaces, true);
        }

        /// <summary>A route that is not added: the SEO slug routes, registered by <see cref="SeoPaths"/>.</summary>
        public static Route CreateLocalizedRoute(this RouteCollection routes, string url, object defaults, string[] namespaces)
        {
            return MapLocalizedRouteInternal(routes, null /* name */, url, defaults, null /* constraints */, namespaces, false);
        }

        private static Route MapLocalizedRouteInternal(RouteCollection routes, string name, string url, object defaults, object constraints, string[] namespaces, bool add)
        {
            var route = new LocalizedRoute(url, new MvcRouteHandler())
            {
                Defaults = new RouteValueDictionary(defaults),
                Constraints = new RouteValueDictionary(constraints),
                DataTokens = new RouteValueDictionary(),
            };

            if (namespaces != null && namespaces.Length > 0)
            {
                route.DataTokens["Namespaces"] = namespaces;
            }

            if (add)
            {
                routes.Add(name, route);
            }

            return route;
        }
    }
}
