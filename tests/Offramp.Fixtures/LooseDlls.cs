using Offramp.Fixtures.Feeds;

namespace Offramp.Fixtures;

/// <summary>The stub DLLs in <c>tests/fixtures/loose-dlls/lib/</c> (see its README).</summary>
public static class LooseDlls
{
    public static string LibPath => RepositoryFiles.Path("tests", "fixtures", "loose-dlls", "lib");

    /// <summary>File name → bytes. Newtonsoft.Json carries the real public key from the versions recording.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        var newtonsoft = VersionsFeed.Load().Packages.Single(p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.1")
            .Files.Single(f => f.Path == "lib/net45/Newtonsoft.Json.dll").Assembly!;
        return new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Newtonsoft.Json.dll"] = StubAssembly.Build(new RecordedAssembly
            {
                Name = "Newtonsoft.Json", Version = "13.0.0.0", PublicKey = newtonsoft.PublicKey, TargetFramework = ".NETFramework,Version=v4.5",
            }),
            ["Legacy.Core.dll"] = StubAssembly.Build(new RecordedAssembly { Name = "Legacy.Core", Version = "1.0.0.0", TargetFramework = ".NETStandard,Version=v2.0" }),
            ["Vendor.Reporting.dll"] = StubAssembly.Build(new RecordedAssembly { Name = "Vendor.Reporting", Version = "2.1.0.0", TargetFramework = ".NETFramework,Version=v4.8" }),
            ["Vendor.Common.dll"] = StubAssembly.Build(new RecordedAssembly { Name = "Vendor.Common", Version = "1.0.0.0", TargetFramework = ".NETStandard,Version=v2.0" }),
        };
    }

    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, bytes) in Build())
        {
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        }
    }
}
