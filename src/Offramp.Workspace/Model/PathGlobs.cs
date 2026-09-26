using Microsoft.Extensions.FileSystemGlobbing;

namespace Offramp.Workspace.Model;

/// <summary>Repository-relative glob matching for <c>paths.exclude</c> and <c>projects[].path</c>.</summary>
public sealed class PathGlobs
{
    private readonly Matcher? _matcher;

    public PathGlobs(IEnumerable<string> patterns)
    {
        var list = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0)
        {
            return;
        }

        _matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        _matcher.AddIncludePatterns(list);
    }

    public bool Matches(string repositoryRelativePath) =>
        _matcher is not null && _matcher.Match(repositoryRelativePath).HasMatches;
}
