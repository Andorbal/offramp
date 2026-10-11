using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Versioning;

namespace Offramp.NuGet.Feeds;

/// <summary>NuGet's global packages folder, as the repository's NuGet configuration names it.</summary>
public static class GlobalPackages
{
    /// <summary>The package's folder there, after extracting <paramref name="nupkg"/> into it when it is not there yet.</summary>
    public static async Task<string> EnsureAsync(string repositoryRoot, string id, NuGetVersion version, byte[] nupkg, CancellationToken cancellationToken)
    {
        var settings = Settings.LoadDefaultSettings(repositoryRoot);
        var folder = SettingsUtility.GetGlobalPackagesFolder(settings);
        var resolver = new VersionFolderPathResolver(folder);
        var installed = resolver.GetInstallPath(id, version);
        if (File.Exists(resolver.GetHashPath(id, version)))
        {
            return installed;
        }

        using var stream = new MemoryStream(nupkg, writable: false);
        using var result = await GlobalPackagesFolderUtility.AddPackageAsync(
            source: "offramp",
            new PackageIdentity(id, version),
            stream,
            folder,
            parentId: Guid.Empty,
            ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance),
            NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);
        return installed;
    }
}
