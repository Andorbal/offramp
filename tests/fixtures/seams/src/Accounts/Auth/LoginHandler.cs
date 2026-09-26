using Accounts.Directory;

namespace Accounts.Auth
{
    /// <summary>Creates its directory itself.</summary>
    public sealed class LoginHandler
    {
        public bool CanLogIn(string samAccountName)
        {
            if (!DirectoryLookup.IsAvailable())
            {
                return false;
            }

            return new DirectoryLookup().FindUser(samAccountName) != null;
        }
    }
}
