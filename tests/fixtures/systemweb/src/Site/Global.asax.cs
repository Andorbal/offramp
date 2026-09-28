using System;
using System.Web;

namespace Site
{
    public class Global : HttpApplication
    {
        protected void Application_BeginRequest(object sender, EventArgs e)
        {
            Response.AppendHeader("X-Greeting", Core.Greeting.For("web"));
        }
    }
}
