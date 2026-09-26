namespace Accounts.Directory
{
    /// <summary>What the rest of the code needs from a directory entry.</summary>
    public sealed class DirectoryEntryInfo
    {
        public string SamAccountName { get; set; }

        public string DisplayName { get; set; }

        public string Email { get; set; }
    }
}
