using System.Web.Routing;
using Store.Framework.Routing;

namespace Store.Web.Infrastructure
{
    public static class SeoRoutes
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            SeoPaths.Register("Product", routes.CreateLocalizedRoute(UrlTemplateFor("Product"), new { controller = "Product", action = "Details" }, new[] { "Store.Web.Controllers" }));
            SeoPaths.Register("Category", routes.CreateLocalizedRoute(UrlTemplateFor("Category"), new { controller = "Catalog", action = "Category" }, new[] { "Store.Web.Controllers" }));

            // The slug routes join the route table here, for URL generation.
            foreach (var path in SeoPaths.All)
            {
                routes.Add(path.EntityName, path.Route);
            }

            string UrlTemplateFor(string entityName)
            {
                return SeoPaths.UrlPrefixFor(entityName) + "/{SeName}";
            }
        }
    }
}
