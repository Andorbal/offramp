using System.Web.Mvc;

namespace Store.Web.Controllers
{
    /// <summary>Actions whose System.Web APIs have ASP.NET Core counterparts: all of them port.</summary>
    public class AccountController : Controller
    {
        public ActionResult Ping()
        {
            return new EmptyResult();
        }

        [HttpPost]
        public ActionResult Save(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                ModelState.AddModelError("name", "A name is required.");
            }

            if (!ModelState.IsValid)
            {
                return new HttpStatusCodeResult(400);
            }

            TempData["saved"] = name;
            return RedirectToAction("Ping");
        }

        public ActionResult Secret()
        {
            return new HttpUnauthorizedResult();
        }

        [HttpPost]
        public ActionResult Post(FormCollection form)
        {
            ViewData["message"] = form["message"];
            return new RedirectResult("/");
        }

        public ActionResult Flag()
        {
            ViewBag.Flag = true;
            return Content(Url.Action("Ping", "Account"));
        }
    }
}
