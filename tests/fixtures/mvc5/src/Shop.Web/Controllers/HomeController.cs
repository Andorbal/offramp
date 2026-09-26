using System;
using System.Web.Mvc;

namespace Shop.Web.Controllers
{
    public class HomeController : Controller
    {
        [OutputCache(Duration = 60)]
        public ActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public ActionResult About()
        {
            ViewBag.Message = "About the shop.";
            return View();
        }

        public ActionResult Catalog(string category)
        {
            return Json(new { category, items = 3 }, JsonRequestBehavior.AllowGet);
        }

        public ActionResult Health()
        {
            return Content("ok");
        }

        // Compiles for .NET Framework only: AppDomainSetup has no ConfigurationFile on .NET.
        public ActionResult Config()
        {
            return Content(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Contact(string message)
        {
            Session["lastMessage"] = message;
            return RedirectToAction("Index");
        }
    }
}
