namespace Evidence.Shop.Controllers
{
    /// <summary>Stands in for System.Web.Mvc.Controller, which the analysis recognizes by name.</summary>
    public abstract class Controller
    {
    }

    public sealed class BoardsController : Controller
    {
        /// <summary>Linked as "ActiveDiscussionsRSS".</summary>
        public string ActiveDiscussionsRss()
        {
            return "rss";
        }

        /// <summary>Linked from nowhere, but MVC reaches actions from a URL.</summary>
        public string SetSellerNote(string note)
        {
            return note;
        }
    }
}
