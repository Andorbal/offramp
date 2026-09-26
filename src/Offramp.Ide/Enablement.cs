namespace Offramp.Ide;

/// <summary>
/// Whether an editor shows anything in a repository (docs/spec/commands/ide.md#when-it-is-on):
/// <c>on</c> and <c>off</c> decide; <c>auto</c> is on when the repository root has an
/// <c>.offramp/</c> directory.
/// </summary>
public static class Enablement
{
    public const string StateDirectoryName = ".offramp";

    public static IdeEnablement Resolve(string repositoryRoot, string? mode)
    {
        var normalized = (mode ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "on" or "true" => new IdeEnablement("on", true, "setting-on"),
            "off" or "false" => new IdeEnablement("off", false, "setting-off"),
            _ => Directory.Exists(Path.Combine(repositoryRoot, StateDirectoryName))
                ? new IdeEnablement("auto", true, "state-directory")
                : new IdeEnablement("auto", false, "no-state-directory"),
        };
    }
}
