using Offramp.Core.Model;

namespace Offramp.NuGet.Inspection;

/// <summary>Where a <c>HintPath</c> comes from when NuGet put the DLL there for packages.config.</summary>
public static class InstalledPackage
{
    /// <summary>
    /// The package in the project's packages.config whose <c>packages/&lt;Id&gt;.&lt;Version&gt;/</c> folder the
    /// <c>HintPath</c> goes through, or null. The folder names the exact package and version; the
    /// assembly version does not (Newtonsoft.Json 13.0.1 to 13.0.3 all ship 13.0.0.0).
    /// </summary>
    public static PackagesConfigPackage? For(ProjectInfo project, string hintPath)
    {
        var segments = hintPath.Replace('\\', '/').Split('/');
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals("packages", StringComparison.OrdinalIgnoreCase)
                && (project.PackagesConfigPackages ?? []).FirstOrDefault(p => string.Equals($"{p.Id}.{p.Version}", segments[i + 1], StringComparison.OrdinalIgnoreCase)) is { } package)
            {
                return package;
            }
        }

        return null;
    }
}
