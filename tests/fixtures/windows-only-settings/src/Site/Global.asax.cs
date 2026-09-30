using System;
using System.Web;

namespace Site
{
    public class Global : HttpApplication
    {
        protected void Application_BeginRequest(object sender, EventArgs e)
        {
            Response.AppendHeader("X-Customers", Legacy.CustomerNames.Format(new Contracts.Order { Customer = "web" }));
        }
    }
}
