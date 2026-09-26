using System.Web;
using System.Web.Http;

namespace Shop.Web.Controllers.Api
{
    public class StockController : ApiController
    {
        public IHttpActionResult Get(int id)
        {
            var user = HttpContext.Current.User.Identity.Name;
            return Ok(new { id, user, available = 7 });
        }
    }
}
