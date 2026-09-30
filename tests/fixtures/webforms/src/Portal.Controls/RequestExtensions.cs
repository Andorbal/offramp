using System.Web;

namespace Portal.Controls
{
    /// <summary>An extension method over a System.Web type, called from another project.</summary>
    public static class RequestExtensions
    {
        public static bool IsSecure(this HttpRequestBase request)
        {
            return request.IsSecureConnection;
        }
    }
}
