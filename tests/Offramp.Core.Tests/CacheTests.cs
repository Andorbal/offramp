using Offramp.Core.Caching;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed class CacheTests
{
    [Fact]
    public void File_cache_round_trips_and_null_cache_stores_nothing()
    {
        using var dir = new ScratchDirectory("cache");
        var cache = new FileCache(dir.Path);

        Assert.False(cache.TryGet("packages/newtonsoft.json", "13.0.3", out _));
        cache.Set("packages/newtonsoft.json", "13.0.3", "{\"ok\":true}");
        Assert.True(cache.TryGet("packages/newtonsoft.json", "13.0.3", out var value));
        Assert.Equal("{\"ok\":true}", value);

        NullCache.Instance.Set("x", "y", "z");
        Assert.False(NullCache.Instance.TryGet("x", "y", out _));
    }

    [Theory]
    [InlineData("..", "k")]
    [InlineData("ns", "../escape")]
    [InlineData("ns", "")]
    public void Keys_cannot_escape_the_cache_directory(string ns, string key)
    {
        using var dir = new ScratchDirectory("cache");
        Assert.Throws<ArgumentException>(() => new FileCache(dir.Path).Set(ns, key, "v"));
    }

    [Fact]
    public void Content_hash_is_lowercase_sha256() =>
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", ContentHash.Sha256("hello"));
}
