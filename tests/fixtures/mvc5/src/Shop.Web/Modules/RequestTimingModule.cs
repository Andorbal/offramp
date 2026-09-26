using System;
using System.Diagnostics;
using System.Web;

namespace Shop.Web.Modules
{
    public class RequestTimingModule : IHttpModule
    {
        public void Init(HttpApplication context)
        {
            context.BeginRequest += (sender, e) => ((HttpApplication)sender).Context.Items["timer"] = Stopwatch.StartNew();
            context.EndRequest += (sender, e) =>
            {
                var application = (HttpApplication)sender;
                var timer = (Stopwatch)application.Context.Items["timer"];
                application.Response.AppendHeader("X-Elapsed-Ms", timer.ElapsedMilliseconds.ToString());
            };
        }

        public void Dispose()
        {
        }
    }
}
