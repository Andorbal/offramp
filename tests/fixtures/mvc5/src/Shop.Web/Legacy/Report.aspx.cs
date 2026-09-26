using System;
using System.Web.UI;

namespace Shop.Web.Legacy
{
    public partial class Report : Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            Title = "Sales report";
        }
    }
}
