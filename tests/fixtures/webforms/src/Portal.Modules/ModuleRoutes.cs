using System.Collections.Generic;
using Portal.Controls;

namespace Portal.Modules
{
    // Named by nothing: RouteRegistry.Discover finds it because it implements IModuleRoutes.
    public class ModuleRoutes : IModuleRoutes
    {
        public void Register(IDictionary<string, string> routes)
        {
            routes["settings"] = "EditSettings.ascx";
        }
    }
}
