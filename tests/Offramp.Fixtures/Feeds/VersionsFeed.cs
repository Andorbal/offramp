namespace Offramp.Fixtures.Feeds;

/// <summary>The recorded feed of the <c>versions</c> fixture (tests/fixtures/versions/feed.json).</summary>
public static class VersionsFeed
{
    private static readonly Lazy<FeedRecording> Recording = new(() => FeedRecording.Load(RecordingPath));

    public static string RecordingPath => RepositoryFiles.Path("tests", "fixtures", "versions", "feed.json");

    public static string SyntheticFeedPath => RepositoryFiles.Path("tests", "fixtures", "versions", "synthetic-feed");

    public static FeedRecording Load() => Recording.Value;

    /// <summary>The synthetic packages only, as the fixture's local feed holds them.</summary>
    public static FeedRecording Synthetic() => Load() with { Packages = [.. Load().Packages.Where(p => p.Synthetic)] };

    /// <summary>Writes the whole recording as a local folder feed and returns its path.</summary>
    public static string WriteFolderFeed(string directory)
    {
        FeedMaterializer.WriteFolderFeed(Load(), directory);
        return directory;
    }
}
