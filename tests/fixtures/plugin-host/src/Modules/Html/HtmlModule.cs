using System.Web;

namespace Modules.Html
{
    public class HtmlModule
    {
        public string Render(HttpContextBase context) => "<p>" + Core.Greeting.For(context.Request.Path) + "</p>";
    }
}
