using System.Collections.Generic;
using Accounts.Directory;

namespace Accounts.Users
{
    /// <summary>Takes its directory through the constructor.</summary>
    public sealed class UserService
    {
        private readonly DirectoryLookup _lookup;

        public UserService(DirectoryLookup lookup)
        {
            _lookup = lookup;
        }

        public string DisplayName(string samAccountName)
        {
            var entry = _lookup.FindUser(samAccountName);
            return entry == null ? samAccountName : entry.DisplayName;
        }

        public bool IsAdmin(string samAccountName)
        {
            List<string> groups = _lookup.GroupsOf(samAccountName);
            return groups.Contains("CN=Admins");
        }

        public void Subscribe()
        {
            _lookup.Watch(change => System.Console.WriteLine(change));
        }
    }
}
