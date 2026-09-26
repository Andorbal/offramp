using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web;
using System.Web.Http;
using System.Web.Mvc;
using System.Web.Routing;

namespace Shop.Web
{
    public class MvcApplication : HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            GlobalConfiguration.Configure(WebApiConfig.Register);
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
        }

        protected void Application_BeginRequest(object sender, EventArgs e)
        {
            Context.Items["started"] = DateTime.UtcNow;
        }

        protected void Session_Start(object sender, EventArgs e)
        {
            Session["cart"] = new List<int>();
        }

        protected void Application_Error(object sender, EventArgs e)
        {
            Trace.TraceError(Server.GetLastError()?.ToString());
        }
    }
}
