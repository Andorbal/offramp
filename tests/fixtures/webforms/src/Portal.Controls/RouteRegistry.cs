using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Portal.Controls
{
    /// <summary>Implemented by modules that add routes; found by reflection, as DotNetNuke finds IServiceRouteMapper.</summary>
    public interface IModuleRoutes
    {
        void Register(IDictionary<string, string> routes);
    }

    public static class RouteRegistry
    {
        public static IDictionary<string, string> Discover(Assembly assembly)
        {
            var routes = new Dictionary<string, string>();
            foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(IModuleRoutes).IsAssignableFrom(t)))
            {
                ((IModuleRoutes)Activator.CreateInstance(type)).Register(routes);
            }

            return routes;
        }
    }
}
