using System;
using System.Collections.Generic;
using Portal.Controls;

namespace Portal.Modules
{
    // Derives from System.Web.UI.UserControl through another project, so on the target every
    // name looked up in here carries "UserControl could not be found" (audit api must not blame them).
    public partial class EditSettings : ModuleBase
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            var count = Convert.ToInt32(ViewState["count"]) + new List<int>().Count;
            var resources = LocalResourceFile(this, "EditSettings.ascx");
            if (count < ModuleId || resources.Length == 0)
            {
                throw new InvalidOperationException(Portal.Utilities.ClientApi.Escape(ClientID));
            }
        }
    }
}
