using System.Web.Mvc;

namespace Shop.Web.Controllers
{
    [Authorize]
    [RoutePrefix("orders")]
    public class OrdersController : Controller
    {
        [Route("")]
        public ActionResult Index()
        {
            return View();
        }

        [Route("{id:int}")]
        public ActionResult Details(int id)
        {
            if (id <= 0)
            {
                return HttpNotFound();
            }

            return Json(new { id, status = "shipped" }, JsonRequestBehavior.AllowGet);
        }
    }
}
