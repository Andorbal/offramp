using System;
using System.Collections.Generic;
using System.DirectoryServices;

namespace Accounts.Directory
{
    /// <summary>Active Directory access: the part that stays on Windows.</summary>
    public class DirectoryLookup
    {
        private readonly DirectoryCache _cache = new DirectoryCache();

        public DirectoryEntryInfo FindUser(string samAccountName)
        {
            using (var searcher = new DirectorySearcher("(sAMAccountName=" + samAccountName + ")"))
            {
                var result = searcher.FindOne();
                if (result == null)
                {
                    return null;
                }

                var entry = new DirectoryEntryInfo { SamAccountName = samAccountName, DisplayName = (string)result.Properties["displayName"][0] };
                _cache.Remember(entry);
                return entry;
            }
        }

        public List<string> GroupsOf(string samAccountName)
        {
            var groups = new List<string>();
            using (var searcher = new DirectorySearcher("(member=" + samAccountName + ")"))
            {
                foreach (SearchResult result in searcher.FindAll())
                {
                    groups.Add(result.Path);
                }
            }

            return groups;
        }

        public void Watch(Action<string> onChange)
        {
            onChange("watching");
        }

        public static bool IsAvailable()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }
    }
}
