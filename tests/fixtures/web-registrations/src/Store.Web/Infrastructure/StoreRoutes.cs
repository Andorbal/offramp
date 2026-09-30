using System.Web.Mvc;
using System.Web.Routing;
using Store.Framework.Routing;

namespace Store.Web.Infrastructure
{
    public static class StoreRoutes
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            routes.IgnoreRoute("{resource}.axd/{*pathInfo}");

            // Media routes: the template starts with a path read from the settings.
            var mediaPath = MediaSettings.PublicPath();

            Route RegisterMediaRoute(string routeName, string actionName, string url)
            {
                return routes.MapRoute(routeName,
                    mediaPath + url + "/{*path}",
                    new { controller = "Media", action = actionName },
                    new[] { "Store.Web.Controllers" });
            }

            RegisterMediaRoute("MediaImage", "Image", "image");
            var tenant = MediaSettings.Tenant() + "/uploaded";
            RegisterMediaRoute("MediaUploaded", "Uploaded", tenant);

            routes.MapLocalizedRoute("HomePage",
                "",
                new { controller = "Home", action = "Index" },
                new[] { "Store.Web.Controllers" });

            routes.MapLocalizedRoute("Login",
                "login/",
                new { controller = "Home", action = "Login" });

            routes.MapLocalizedRoute("Wishlist",
                "wishlist/{customerGuid}",
                new { controller = "Home", action = "Wishlist", customerGuid = UrlParameter.Optional },
                new { customerGuid = @"[0-9a-f\-]+" },
                new[] { "Store.Web.Controllers" });

            routes.MapRoute("Default",
                "{controller}/{action}/{id}",
                new { controller = "Home", action = "Index", id = UrlParameter.Optional },
                new[] { "Store.Web.Controllers" });
        }
    }
}
