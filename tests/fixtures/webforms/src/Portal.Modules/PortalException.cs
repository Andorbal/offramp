using System.Web;

namespace Portal.Modules
{
    // Derives from HttpException, which the target does not have. ErrorCode, which it inherits
    // from ExternalException, exists on the target: only HttpException is missing.
    public class PortalException : HttpException
    {
        public bool IsAccessDenied()
        {
            return ErrorCode == unchecked((int)0x80070005);
        }
    }
}
