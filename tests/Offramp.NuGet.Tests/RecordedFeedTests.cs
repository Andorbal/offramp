using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;

namespace Offramp.NuGet.Tests;

public sealed class RecordedFeedTests
{
    /// <summary>
    /// The versions fixture restores its synthetic packages from committed .nupkg files;
    /// they must be exactly what the recording materializes to. Regenerate with
    /// <c>OFFRAMP_REGENERATE=1 dotnet test --filter Synthetic_feed</c>.
    /// </summary>
    [Fact]
    public void Synthetic_feed_matches_the_recording()
    {
        var directory = VersionsFeed.SyntheticFeedPath;
        var synthetic = VersionsFeed.Synthetic();
        if (Environment.GetEnvironmentVariable("OFFRAMP_REGENERATE") == "1")
        {
            foreach (var stale in Directory.EnumerateFiles(directory, "*.nupkg"))
            {
                File.Delete(stale);
            }

            FeedMaterializer.WriteFolderFeed(synthetic, directory);
        }

        var expected = synthetic.Packages.ToDictionary(p => $"{p.Id.ToLowerInvariant()}.{p.Version}.nupkg", FeedMaterializer.Nupkg);
        var actual = Directory.EnumerateFiles(directory, "*.nupkg").ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in expected)
        {
            // Entry by entry, not byte by byte: the zip writer records the OS it ran on.
            Assert.True(Entries(bytes).SequenceEqual(Entries(actual[name])), $"{name} differs from the recording; regenerate it.");
        }
    }

    /// <summary>
    /// The loose-dlls fixture's stub DLLs are what <see cref="LooseDlls"/> writes; regenerate with
    /// <c>OFFRAMP_REGENERATE=1 dotnet test --filter Loose_dlls</c>.
    /// </summary>
    [Fact]
    public void Loose_dlls_match_their_generator()
    {
        if (Environment.GetEnvironmentVariable("OFFRAMP_REGENERATE") == "1")
        {
            LooseDlls.Write(LooseDlls.LibPath);
        }

        var expected = LooseDlls.Build();
        var actual = Directory.EnumerateFiles(LooseDlls.LibPath, "*.dll").ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);

        Assert.Equal(expected.Keys, actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in expected)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(actual[name]), $"{name} differs from its generator; regenerate it.");
        }
    }

    private static List<string> Entries(byte[] nupkg)
    {
        using var zip = new ZipArchive(new MemoryStream(nupkg), ZipArchiveMode.Read);
        return [.. zip.Entries.Select(e =>
        {
            using var content = new MemoryStream();
            using (var stream = e.Open())
            {
                stream.CopyTo(content);
            }

            return $"{e.FullName} {e.LastWriteTime.DateTime:yyyy-MM-ddTHH:mm:ss} {Convert.ToHexString(SHA256.HashData(content.ToArray()))}";
        })];
    }

    [Fact]
    public void Materializing_is_deterministic()
    {
        var package = VersionsFeed.Load().Packages.Single(p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3");

        Assert.Equal(FeedMaterializer.Nupkg(package), FeedMaterializer.Nupkg(package));
    }

    [Fact]
    public void Stub_assemblies_keep_the_recorded_identity_references_and_platforms()
    {
        var drawing = VersionsFeed.Load().Packages.Single(p => p.Id == "System.Drawing.Common").Files
            .Single(f => f.Path == "lib/net8.0/System.Drawing.Common.dll").Assembly!;

        using var pe = new PEReader(new MemoryStream(StubAssembly.Build(drawing)));
        var metadata = pe.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();

        Assert.Equal("System.Drawing.Common", metadata.GetString(definition.Name));
        Assert.Equal(new Version(drawing.Version), definition.Version);
        Assert.Equal(drawing.PublicKey, Convert.ToHexString(metadata.GetBlobBytes(definition.PublicKey)).ToLowerInvariant());
        Assert.Equal(drawing.References.Select(r => r.Name),
            metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).Where(n => n != "System.Runtime" || drawing.References.Any(r => r.Name == "System.Runtime")));
        Assert.NotNull(Offramp.NuGet.Inspection.PackageInspector.WindowsEvidence(StubAssembly.Build(drawing)));
    }
}
