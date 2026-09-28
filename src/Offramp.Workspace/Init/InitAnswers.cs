using NuGet.Packaging;
using NuGet.Versioning;
using Offramp.Core.Configuration;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Init;

/// <summary>A typed answer turned into a configuration value, or the reason it cannot be one.</summary>
public sealed record InitAnswer<T>(T? Value, string? Error)
{
    public static InitAnswer<T> Ok(T value) => new(value, null);

    public static InitAnswer<T> Invalid(string error) => new(default, error);
}

/// <summary>The central file answer: <c>deps.cpm.file</c> and the <c>deps.cpm.scope</c> that keeps it where it was typed.</summary>
public sealed record CpmLocation(string File, string Scope);

/// <summary>
/// Reads the <c>init</c> interview's free-text answers. Paths are typed from the repository root
/// (absolute paths inside it are accepted too) and come back repository-relative.
/// </summary>
public static class InitAnswers
{
    /// <summary>
    /// Where <c>deps consolidate --cpm</c> writes central versions. A folder gets
    /// <c>Directory.Packages.props</c> in it. A bare name means the repository root, which
    /// <c>scope: repo</c> keeps even when the solution is in a folder.
    /// </summary>
    public static InitAnswer<CpmLocation> CpmFile(string repositoryRoot, string answer)
    {
        var path = RepositoryPath(repositoryRoot, answer);
        if (path.Error is not null)
        {
            return InitAnswer<CpmLocation>.Invalid(path.Error);
        }

        var file = path.Value!;
        if (file.Length == 0 || answer.TrimEnd().EndsWith('/') || answer.TrimEnd().EndsWith('\\') || Directory.Exists(RepoPaths.ToAbsolute(repositoryRoot, file)))
        {
            file = file.Length == 0 ? CpmConfig.DefaultFile : file.TrimEnd('/') + "/" + CpmConfig.DefaultFile;
        }

        if (!file.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
        {
            return InitAnswer<CpmLocation>.Invalid("Use a .props file, for example src/Directory.Packages.props, or a folder.");
        }

        return InitAnswer<CpmLocation>.Ok(new CpmLocation(file, file.Contains('/', StringComparison.Ordinal) ? "solution" : "repo"));
    }

    /// <summary>A pinned project: null (every project) when empty, else an existing project file.</summary>
    public static InitAnswer<string?> Project(string repositoryRoot, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return InitAnswer<string?>.Ok(null);
        }

        var path = RepositoryPath(repositoryRoot, answer);
        if (path.Error is not null)
        {
            return InitAnswer<string?>.Invalid(path.Error);
        }

        var project = path.Value!;
        return project.EndsWith("proj", StringComparison.OrdinalIgnoreCase) && File.Exists(RepoPaths.ToAbsolute(repositoryRoot, project))
            ? InitAnswer<string?>.Ok(project)
            : InitAnswer<string?>.Invalid($"No project file at {project}. Give the path of a .csproj from the repository root, or press Enter for every project.");
    }

    /// <summary>A NuGet package id, as nuget.org shows it.</summary>
    public static InitAnswer<string> PackageId(string answer)
    {
        var id = answer.Trim();
        return PackageIdValidator.IsValidPackageId(id)
            ? InitAnswer<string>.Ok(id)
            : InitAnswer<string>.Invalid("That is not a NuGet package id. Use the name nuget.org shows, for example Newtonsoft.Json.");
    }

    /// <summary>A NuGet version to pin, for example <c>9.0.1</c>.</summary>
    public static InitAnswer<string> Version(string answer)
    {
        var text = answer.Trim();
        return NuGetVersion.TryParse(text, out _)
            ? InitAnswer<string>.Ok(text)
            : InitAnswer<string>.Invalid("That is not a version. Use one NuGet version, for example 9.0.1.");
    }

    private static InitAnswer<string> RepositoryPath(string repositoryRoot, string answer)
    {
        var text = answer.Trim();
        var absolute = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(repositoryRoot, text.Replace('\\', '/')));
        var relative = RepoPaths.ToRepositoryRelative(repositoryRoot, absolute);
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? InitAnswer<string>.Invalid("That path is outside the repository. Give a path from the repository root.")
            : InitAnswer<string>.Ok(relative);
    }
}
