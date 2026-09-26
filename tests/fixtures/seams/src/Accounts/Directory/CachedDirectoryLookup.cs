namespace Accounts.Directory
{
    /// <summary>Inherits the directory dependency from its base type.</summary>
    public sealed class CachedDirectoryLookup : DirectoryLookup
    {
        public int Hits { get; private set; }
    }
}
