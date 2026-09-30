using System.Web.Http;
using System.Web.OData.Builder;
using System.Web.OData.Extensions;

namespace Store.Framework.WebApi
{
    /// <summary>Web API and OData routes, registered by the framework library for every site that uses it.</summary>
    public static class WebApiStartup
    {
        public static void Register(HttpConfiguration config)
        {
            if (!config.Routes.ContainsKey(WebApiNames.DefaultApi))
            {
                config.Routes.MapHttpRoute(WebApiNames.DefaultApi, "api/{version}/{controller}/{id}",
                    new { version = "v1", id = RouteParameter.Optional });
            }

            var builder = new ODataConventionModelBuilder();
            config.MapODataServiceRoute(WebApiNames.OData, WebApiNames.ODataPath, builder.GetEdmModel());
        }
    }
}
