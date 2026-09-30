using System.Web;
using Portal.Controls;

namespace Portal.Modules
{
    // IsSecure is Portal.Controls' extension of HttpRequestBase: it is missing on the target
    // because HttpRequestBase (System.Web) is, so audit api attributes it to System.Web.
    public class LinkBuilder
    {
        private readonly HttpContextBase _httpContext;

        public LinkBuilder(HttpContextBase httpContext)
        {
            _httpContext = httpContext;
        }

        public string Scheme()
        {
            return _httpContext.Request.IsSecure() ? "https" : "http";
        }
    }
}
