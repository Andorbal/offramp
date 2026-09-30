using System.Web.Mvc;
using System.Web.Routing;
using Store.Framework.Routing;

namespace Store.Web.Infrastructure
{
    /// <summary>Areas declared by their routes, not by AreaRegistration classes.</summary>
    public static class AreaRoutes
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            // The admin area: an area default.
            routes.MapRoute("Admin_default",
                "Admin/{controller}/{action}/{id}",
                new { controller = "Dashboard", action = "Index", area = "Admin", id = "" },
                new[] { "Store.Web.Areas.Admin.Controllers" });

            // A plugin's area: a data token on the route.
            routes.MapRoute("Store.Tax.Configure",
                "Plugins/Store.Tax/{action}",
                new { controller = "TaxSettings", action = "Configure" },
                new[] { "Store.Tax.Controllers" })
                .DataTokens["area"] = "Store.Tax";

            // An area through the framework's helper.
            routes.MapAreaRoute("Reports", "Reports_default", "reports/{action}", new { controller = "Report", action = "Index" });
        }
    }
}
