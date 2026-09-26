using System.Collections.Generic;
using System.DirectoryServices;

namespace Accounts.Directory
{
    internal sealed class DirectoryCache
    {
        private readonly Dictionary<string, DirectoryEntryInfo> _entries = new Dictionary<string, DirectoryEntryInfo>();

        public DirectoryEntry Root { get; } = new DirectoryEntry();

        public void Remember(DirectoryEntryInfo entry)
        {
            _entries[entry.SamAccountName] = entry;
        }
    }
}
