using System;
using System.Web.UI;

namespace Portal.Modules
{
    // Named only by Default.aspx's Inherits attribute.
    public partial class DefaultPage : Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            Title = "Portal " + Portal.Controls.RouteRegistry.Discover(GetType().Assembly).Count;
        }
    }
}
