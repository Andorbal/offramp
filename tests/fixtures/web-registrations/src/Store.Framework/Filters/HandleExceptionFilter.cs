using System.Web.Mvc;

namespace Store.Framework.Filters
{
    public class HandleExceptionFilter : IExceptionFilter, IActionFilter
    {
        public void OnException(ExceptionContext filterContext)
        {
        }

        public void OnActionExecuting(ActionExecutingContext filterContext)
        {
        }

        public void OnActionExecuted(ActionExecutedContext filterContext)
        {
        }
    }
}
