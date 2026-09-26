// Records the structure of real NuGet packages into a feed recording that tests replay
// offline (tests/Offramp.Fixtures/Feeds). Run from the repository root:
//
//   dotnet run eng/record-feed.cs -- tests/fixtures/versions/feed.seeds.json tests/fixtures/versions/feed.json
//
// The seeds list package versions to record and the target frameworks whose dependency
// closure must be present so the fixture restores offline. Synthetic packages in the seeds
// are copied through as written.
#:project ../tests/Offramp.Fixtures/Offramp.Fixtures.csproj
#:property TargetFramework=net10.0
#:property TreatWarningsAsErrors=false
#:property EnableNETAnalyzers=false
#:property NoWarn=CS1591
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Offramp.Fixtures.Feeds;

var seeds = JsonSerializer.Deserialize<Seeds>(File.ReadAllText(args[0]), FeedRecording.JsonOptions)!;
var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
var cache = new SourceCacheContext();
var byId = await repository.GetResourceAsync<FindPackageByIdResource>();
var metadata = await repository.GetResourceAsync<PackageMetadataResource>();
var frameworks = seeds.Frameworks.Select(NuGetFramework.Parse).ToList();

var recorded = new SortedDictionary<string, RecordedPackage>(StringComparer.OrdinalIgnoreCase);
var pending = new Queue<PackageIdentity>(seeds.Packages.SelectMany(p => p.Versions.Select(v => new PackageIdentity(p.Id, NuGetVersion.Parse(v)))));
foreach (var synthetic in seeds.Synthetic)
{
    recorded[Key(synthetic.Id, synthetic.Version)] = synthetic with { Synthetic = true };
    foreach (var dependency in Closure(synthetic.DependencyGroups))
    {
        pending.Enqueue(await Resolve(dependency));
    }
}

while (pending.Count > 0)
{
    var identity = pending.Dequeue();
    var key = Key(identity.Id, identity.Version.ToNormalizedString());
    if (recorded.ContainsKey(key))
    {
        continue;
    }

    Console.Error.WriteLine($"recording {identity}");
    var package = await Record(identity);
    recorded[key] = package;
    foreach (var dependency in Closure(package.DependencyGroups))
    {
        pending.Enqueue(await Resolve(dependency));
    }
}

var recording = new FeedRecording
{
    Source = "https://api.nuget.org/v3/index.json",
    RecordedAt = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    Packages = [.. recorded.Values],
};
File.WriteAllText(args[1], recording.ToJson());
Console.Error.WriteLine($"wrote {recorded.Count} packages to {args[1]}");

IEnumerable<RecordedDependency> Closure(IReadOnlyList<RecordedDependencyGroup> groups)
{
    var parsed = groups.Select(g => new FrameworkGroup(g.TargetFramework.Length == 0 ? NuGetFramework.AnyFramework : NuGetFramework.Parse(g.TargetFramework), g)).ToList();
    foreach (var framework in frameworks)
    {
        var nearest = NuGetFrameworkUtility.GetNearest(parsed, framework, p => p.Framework);
        foreach (var dependency in nearest?.Group.Dependencies ?? [])
        {
            yield return dependency;
        }
    }
}

async Task<PackageIdentity> Resolve(RecordedDependency dependency)
{
    var range = VersionRange.Parse(dependency.Range);
    var versions = await byId.GetAllVersionsAsync(dependency.Id, cache, NullLogger.Instance, CancellationToken.None);
    var chosen = range.MinVersion is not null && range.IsMinInclusive && versions.Contains(range.MinVersion)
        ? range.MinVersion
        : versions.Where(range.Satisfies).Min() ?? throw new InvalidOperationException($"No version of {dependency.Id} satisfies {range}.");
    return new PackageIdentity(dependency.Id, chosen);
}

async Task<RecordedPackage> Record(PackageIdentity identity)
{
    using var stream = new MemoryStream();
    if (!await byId.CopyNupkgToStreamAsync(identity.Id, identity.Version, stream, cache, NullLogger.Instance, CancellationToken.None))
    {
        throw new InvalidOperationException($"{identity} was not found.");
    }

    stream.Position = 0;
    using var reader = new PackageArchiveReader(stream);
    var nuspec = reader.NuspecReader;
    var files = new List<RecordedFile>();
    foreach (var path in reader.GetFiles().Order(StringComparer.Ordinal))
    {
        if (RecordedFileFor(reader, path) is { } file)
        {
            files.Add(file);
        }
    }

    var search = await metadata.GetMetadataAsync(identity, cache, NullLogger.Instance, CancellationToken.None);
    var deprecation = search is null ? null : await search.GetDeprecationMetadataAsync();
    return new RecordedPackage
    {
        Id = nuspec.GetId(),
        Version = nuspec.GetVersion().ToNormalizedString(),
        Listed = search?.IsListed ?? true,
        Authors = nuspec.GetAuthors(),
        Deprecation = deprecation is null ? null : new RecordedDeprecation(
            [.. deprecation.Reasons.Order(StringComparer.Ordinal)], deprecation.Message,
            deprecation.AlternatePackage?.PackageId, deprecation.AlternatePackage?.Range?.ToNormalizedString()),
        DependencyGroups = [.. nuspec.GetDependencyGroups().Select(g => new RecordedDependencyGroup(
            g.TargetFramework.IsAny ? "" : g.TargetFramework.GetShortFolderName(),
            [.. g.Packages.Select(p => new RecordedDependency(p.Id, p.VersionRange.ToNormalizedString()))]))],
        Files = files,
    };
}

static RecordedFile? RecordedFileFor(PackageArchiveReader reader, string path)
{
    var roots = new[] { "lib/", "ref/", "runtimes/", "build/", "buildTransitive/", "contentFiles/" };
    if (!roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
    {
        return null;
    }

    var extension = Path.GetExtension(path).ToLowerInvariant();
    if (extension is ".xml" or ".pdb" or ".md" or ".txt" or ".png" or ".jpg" or ".snk" or ".p7s" or ".psd1" or ".psm1")
    {
        return null;
    }

    using var entry = reader.GetStream(path);
    using var buffer = new MemoryStream();
    entry.CopyTo(buffer);
    var bytes = buffer.ToArray();
    if (extension is ".dll" or ".exe" or ".winmd")
    {
        return new RecordedFile { Path = path, Assembly = ReadAssembly(bytes) };
    }

    if (extension is ".props" or ".targets" || (bytes.Length < 65536 && path.StartsWith("contentFiles/", StringComparison.OrdinalIgnoreCase)))
    {
        return new RecordedFile { Path = path, Content = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal) };
    }

    return new RecordedFile { Path = path };
}

static RecordedAssembly? ReadAssembly(byte[] bytes)
{
    using var pe = new PEReader(new MemoryStream(bytes));
    if (!pe.HasMetadata)
    {
        return null;
    }

    var md = pe.GetMetadataReader();
    if (!md.IsAssembly)
    {
        return null;
    }

    var definition = md.GetAssemblyDefinition();
    var platforms = new List<string>();
    foreach (var handle in definition.GetCustomAttributes())
    {
        var attribute = md.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind == HandleKind.MemberReference
            && md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
            && md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) == "SupportedOSPlatformAttribute")
        {
            var blob = md.GetBlobReader(attribute.Value);
            blob.ReadUInt16();
            if (blob.ReadSerializedString() is { } platform)
            {
                platforms.Add(platform);
            }
        }
    }

    var publicKey = md.GetBlobBytes(definition.PublicKey);
    return new RecordedAssembly
    {
        Name = md.GetString(definition.Name),
        Version = definition.Version.ToString(),
        Culture = definition.Culture.IsNil || md.GetString(definition.Culture).Length == 0 ? null : md.GetString(definition.Culture),
        PublicKey = publicKey.Length == 0 ? null : Convert.ToHexString(publicKey).ToLowerInvariant(),
        References = [.. md.AssemblyReferences.Select(h => md.GetAssemblyReference(h)).Select(r => new RecordedAssemblyReference(
            md.GetString(r.Name), r.Version.ToString(),
            r.PublicKeyOrToken.IsNil ? null : Token(md.GetBlobBytes(r.PublicKeyOrToken), r.Flags))).OrderBy(r => r.Name, StringComparer.Ordinal)],
        SupportedOSPlatforms = [.. platforms.Order(StringComparer.Ordinal)],
    };
}

static string? Token(byte[] keyOrToken, System.Reflection.AssemblyFlags flags)
{
    if ((flags & System.Reflection.AssemblyFlags.PublicKey) == 0)
    {
        return Convert.ToHexString(keyOrToken).ToLowerInvariant();
    }

#pragma warning disable CA5350 // the public key token is defined as the last 8 bytes of the key's SHA-1, reversed
    var hash = System.Security.Cryptography.SHA1.HashData(keyOrToken);
#pragma warning restore CA5350
    return Convert.ToHexString(hash[^8..].Reverse().ToArray()).ToLowerInvariant();
}

static string Key(string id, string version) => id.ToLowerInvariant() + "/" + NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant();

sealed record Seeds(IReadOnlyList<string> Frameworks, IReadOnlyList<SeedPackage> Packages, IReadOnlyList<RecordedPackage> Synthetic);

sealed record SeedPackage(string Id, IReadOnlyList<string> Versions);

sealed record FrameworkGroup(NuGetFramework Framework, RecordedDependencyGroup Group);
