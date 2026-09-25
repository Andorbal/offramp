using System.Security.Cryptography;
using System.Text;

namespace Offramp.Core.Caching;

/// <summary>
/// Content-addressed cache under <c>.offramp/cache/</c>. Entries are keyed by a
/// namespace and a key (usually a hash of the inputs); a published NuGet
/// version is immutable, so entries never expire. <c>--no-cache</c> swaps in
/// <see cref="NullCache"/>.
/// </summary>
public interface ICache
{
    bool TryGet(string ns, string key, out string value);

    void Set(string ns, string key, string value);
}

/// <summary>A cache that stores nothing.</summary>
public sealed class NullCache : ICache
{
    public static readonly NullCache Instance = new();

    public bool TryGet(string ns, string key, out string value)
    {
        value = "";
        return false;
    }

    public void Set(string ns, string key, string value)
    {
    }
}

/// <summary>Files under a directory: <c>&lt;root&gt;/&lt;ns&gt;/&lt;key&gt;.json</c>.</summary>
public sealed class FileCache(string root) : ICache
{
    public string Root { get; } = root;

    public bool TryGet(string ns, string key, out string value)
    {
        var path = PathFor(ns, key);
        if (!File.Exists(path))
        {
            value = "";
            return false;
        }

        value = File.ReadAllText(path, Encoding.UTF8);
        return true;
    }

    public void Set(string ns, string key, string value)
    {
        var path = PathFor(ns, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write then rename so a concurrent reader never sees a partial entry.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, value, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string ns, string key)
    {
        foreach (var part in new[] { ns, key })
        {
            if (part.Length == 0 || part.Contains("..", StringComparison.Ordinal) || part.IndexOfAny(['\\', ':']) >= 0)
            {
                throw new ArgumentException($"Invalid cache key part '{part}'.");
            }
        }

        return Path.Combine(Root, ns.Replace('/', Path.DirectorySeparatorChar), key + ".json");
    }
}

public static class ContentHash
{
    /// <summary>SHA-256 of UTF-8 text, lowercase hex.</summary>
    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
