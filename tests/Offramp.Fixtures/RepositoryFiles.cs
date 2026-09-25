namespace Offramp.Fixtures;

/// <summary>Locates files of the Offramp repository itself (schemas, docs, sources) from a test.</summary>
public static class RepositoryFiles
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    public static string Path(params string[] relative) => System.IO.Path.Combine([Root, .. relative]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Offramp.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Offramp.slnx) above " + AppContext.BaseDirectory);
    }
}
