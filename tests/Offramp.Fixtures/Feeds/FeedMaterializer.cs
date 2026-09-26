using System.IO.Compression;
using System.Security;
using System.Text;

namespace Offramp.Fixtures.Feeds;

/// <summary>Writes a recording as .nupkg files, byte-identical on every run and OS.</summary>
public static class FeedMaterializer
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Writes every package into <paramref name="directory"/> (a local folder feed) and returns the paths.</summary>
    public static IReadOnlyList<string> WriteFolderFeed(FeedRecording recording, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var package in recording.Packages)
        {
            var path = Path.Combine(directory, $"{package.Id.ToLowerInvariant()}.{package.Version.ToLowerInvariant()}.nupkg");
            File.WriteAllBytes(path, Nupkg(package));
            written.Add(path);
        }

        return written;
    }

    public static byte[] Nupkg(RecordedPackage package)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, package.Id + ".nuspec", Encoding.UTF8.GetBytes(Nuspec(package)));
            foreach (var file in package.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                var bytes = file.Assembly is not null ? StubAssembly.Build(file.Assembly)
                    : file.Content is not null ? Encoding.UTF8.GetBytes(file.Content)
                    : [];
                Add(zip, file.Path, bytes);
            }
        }

        return stream.ToArray();
    }

    public static string Nuspec(RecordedPackage package)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        builder.Append("<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n  <metadata>\n");
        builder.Append("    <id>").Append(Xml(package.Id)).Append("</id>\n");
        builder.Append("    <version>").Append(Xml(package.Version)).Append("</version>\n");
        builder.Append("    <authors>").Append(Xml(package.Authors.Length > 0 ? package.Authors : "recorded")).Append("</authors>\n");
        builder.Append("    <description>").Append(Xml(package.Description.Length > 0 ? package.Description : package.Id)).Append("</description>\n");
        if (package.DependencyGroups.Count > 0)
        {
            builder.Append("    <dependencies>\n");
            foreach (var group in package.DependencyGroups)
            {
                builder.Append("      <group").Append(group.TargetFramework.Length > 0 ? $" targetFramework=\"{Xml(group.TargetFramework)}\"" : "").Append(">\n");
                foreach (var dependency in group.Dependencies)
                {
                    builder.Append("        <dependency id=\"").Append(Xml(dependency.Id)).Append("\" version=\"").Append(Xml(dependency.Range)).Append("\" />\n");
                }

                builder.Append("      </group>\n");
            }

            builder.Append("    </dependencies>\n");
        }

        builder.Append("  </metadata>\n</package>\n");
        return builder.ToString();
    }

    private static void Add(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = Timestamp;
        using var target = entry.Open();
        target.Write(bytes);
    }

    private static string Xml(string value) => SecurityElement.Escape(value) ?? "";
}
