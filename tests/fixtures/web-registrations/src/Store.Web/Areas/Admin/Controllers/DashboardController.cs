using System.Web.Mvc;

namespace Store.Web.Areas.Admin.Controllers
{
    public class DashboardController : Controller
    {
        public ActionResult Index()
        {
            return Content("dashboard");
        }
    }
}
