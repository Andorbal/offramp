using System.Web.Mvc;
using Store.Framework.Controllers;

namespace Store.Web.Controllers
{
    /// <summary>A controller on the framework's base class, which the container's filters apply to.</summary>
    public class HomeController : PublicControllerBase
    {
        public ActionResult Index()
        {
            return View();
        }
    }
}
