using System.Web.UI;

namespace Portal.Controls
{
    /// <summary>What every module control derives from, as in DotNetNuke's PortalModuleBase.</summary>
    public class ModuleBase : UserControl
    {
        public int ModuleId { get; set; }

        public static string LocalResourceFile(Control control, string name)
        {
            return control.TemplateSourceDirectory + "/App_LocalResources/" + name;
        }
    }
}
