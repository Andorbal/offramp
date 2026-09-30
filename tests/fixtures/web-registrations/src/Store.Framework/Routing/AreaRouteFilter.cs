using System;
using System.Web.Routing;

namespace Store.Framework.Routing
{
    public static class AreaRouteFilter
    {
        /// <summary>A copy of the route table with one area's routes, for URL generation: not a registration.</summary>
        public static RouteCollection ForArea(RouteCollection routes, string areaName)
        {
            var filtered = new RouteCollection();
            foreach (RouteBase route in routes)
            {
                if (route is Route candidate && string.Equals(candidate.DataTokens?["area"] as string, areaName, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(route);
                }
            }

            return filtered;
        }
    }
}
