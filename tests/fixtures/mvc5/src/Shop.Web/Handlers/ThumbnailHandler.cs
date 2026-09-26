using System.Web;

namespace Shop.Web.Handlers
{
    public class ThumbnailHandler : IHttpHandler
    {
        public bool IsReusable => true;

        public void ProcessRequest(HttpContext context)
        {
            context.Response.ContentType = "image/png";
            context.Response.BinaryWrite(new byte[0]);
        }
    }
}
