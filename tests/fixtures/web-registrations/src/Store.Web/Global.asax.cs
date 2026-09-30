using System.Web;
using System.Web.Http;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using Autofac;
using Store.Framework.WebApi;
using Store.Web.Infrastructure;

namespace Store.Web
{
    public class StoreApplication : HttpApplication
    {
        protected void Application_Start()
        {
            GlobalFilters.Filters.Add(new HandleErrorAttribute());
            GlobalConfiguration.Configure(WebApiStartup.Register);
            StoreRoutes.RegisterRoutes(RouteTable.Routes);
            SeoRoutes.RegisterRoutes(RouteTable.Routes);
            AreaRoutes.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);

            var builder = new ContainerBuilder();
            Store.Framework.DependencyRegistrar.Register(builder);
            DependencyRegistrar.Register(builder);
        }
    }
}
