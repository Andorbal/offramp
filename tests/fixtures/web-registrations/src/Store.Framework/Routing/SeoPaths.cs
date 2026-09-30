using System.Collections.Generic;
using System.Web.Routing;

namespace Store.Framework.Routing
{
    /// <summary>Routes for SEO slugs, created first and added to the route table later.</summary>
    public static class SeoPaths
    {
        private static readonly List<SeoPath> Registered = new List<SeoPath>();

        public static IEnumerable<SeoPath> All => Registered;

        public static void Register(string entityName, Route route)
        {
            Registered.Add(new SeoPath { EntityName = entityName, Route = route });
        }

        public static string UrlPrefixFor(string entityName)
        {
            return entityName.ToLowerInvariant();
        }
    }

    public class SeoPath
    {
        public string EntityName { get; set; }

        public Route Route { get; set; }
    }
}
