using Offramp.Core.Processes;

namespace Offramp.Workspace.Environment;

/// <summary>How an MSBuild.exe was found.</summary>
public enum MsbuildSource
{
    /// <summary><c>--msbuild-path</c> or <c>scan.msbuildPath</c>.</summary>
    Configured,

    /// <summary>The Visual Studio installation of a Developer Command Prompt (<c>VSINSTALLDIR</c>).</summary>
    DeveloperPrompt,

    /// <summary>The newest Visual Studio or Build Tools installation with MSBuild, as vswhere reports it.</summary>
    Vswhere,
}

/// <summary>The MSBuild.exe to run, or why there is none.</summary>
/// <param name="Path">Absolute path of MSBuild.exe; null when none was found.</param>
/// <param name="Source">How it was found; null when it was not.</param>
/// <param name="NotFound">Where Offramp looked, as a sentence, when nothing was found.</param>
public sealed record MsbuildLocation(string? Path, MsbuildSource? Source, string? NotFound)
{
    public static MsbuildLocation At(string path, MsbuildSource source) => new(path, source, null);

    public static MsbuildLocation Missing(string reason) => new(null, null, reason);
}

/// <summary>
/// Finds MSBuild.exe for <c>scan --msbuild</c> (docs/spec/commands/workspace.md#scan): the
/// configured path, else the Developer Command Prompt's installation, else vswhere. <c>PATH</c>
/// is not searched: it often holds the .NET Framework's MSBuild 4, which builds nothing modern.
/// </summary>
public static class MsbuildLocator
{
    public const string FileName = "MSBuild.exe";

    /// <summary>Where MSBuild.exe sits in a Visual Studio or Build Tools installation, newest layout first.</summary>
    public static readonly IReadOnlyList<string> InstallationLayouts = ["MSBuild/Current/Bin", "MSBuild/15.0/Bin"];

    /// <summary>Where MSBuild.exe may sit in a given folder: directly inside it, then in an installation's layout.</summary>
    private static readonly IReadOnlyList<string> FolderLayouts = ["", .. InstallationLayouts];

    private static readonly TimeSpan VswhereTimeout = TimeSpan.FromSeconds(60);

    /// <param name="configured">A path from <c>--msbuild-path</c> or <c>scan.msbuildPath</c>, relative to <paramref name="baseDirectory"/>.</param>
    /// <param name="baseDirectory">What a relative <paramref name="configured"/> path is relative to.</param>
    /// <param name="environment">The process environment (<c>VSINSTALLDIR</c>, <c>ProgramFiles(x86)</c>).</param>
    /// <param name="processes">Runs vswhere.</param>
    /// <param name="cancellationToken">Stops the vswhere query.</param>
    public static async Task<MsbuildLocation> LocateAsync(
        string? configured,
        string baseDirectory,
        IReadOnlyDictionary<string, string> environment,
        IProcessRunner processes,
        CancellationToken cancellationToken)
    {
        if (configured is not null)
        {
            return FromFileOrFolder(Path.GetFullPath(configured, baseDirectory)) is { } path
                ? MsbuildLocation.At(path, MsbuildSource.Configured)
                : MsbuildLocation.Missing(
                    $"'{configured}' is neither {FileName} nor a folder holding it directly or under {string.Join(" or ", InstallationLayouts)}.");
        }

        var installation = Variable(environment, "VSINSTALLDIR");
        if (installation is not null && FromFileOrFolder(installation) is { } prompt)
        {
            return MsbuildLocation.At(prompt, MsbuildSource.DeveloperPrompt);
        }

        var programFiles = Variable(environment, "ProgramFiles(x86)");
        if (programFiles is null)
        {
            return MsbuildLocation.Missing(
                $"{FileName} comes with Visual Studio or the Build Tools on Windows, and there is no installation to look in here (no ProgramFiles(x86) folder).");
        }

        var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (await FromVswhereAsync(vswhere, processes, cancellationToken) is { } installed)
        {
            return MsbuildLocation.At(installed, MsbuildSource.Vswhere);
        }

        const string noInstallation = "no Visual Studio or Build Tools installation with the MSBuild component";
        return MsbuildLocation.Missing(installation is null
            ? $"vswhere reports {noInstallation}."
            : $"The Developer Command Prompt's installation ({installation}) has no {FileName}, and vswhere reports {noInstallation}.");
    }

    /// <summary>MSBuild.exe itself, or the one in a folder: directly inside it, or in an installation's layout.</summary>
    internal static string? FromFileOrFolder(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        return FolderLayouts
            .Select(layout => Path.GetFullPath(Path.Combine(path, layout, FileName)))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<string?> FromVswhereAsync(string vswhere, IProcessRunner processes, CancellationToken cancellationToken)
    {
        // The query vswhere documents for MSBuild: the newest release installation, Build Tools included.
        var result = await processes.RunAsync(new ProcessSpec(vswhere,
        [
            "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe",
        ])
        {
            Timeout = VswhereTimeout,
        }, cancellationToken);
        if (!result.Succeeded)
        {
            return null;
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(File.Exists);
    }

    /// <summary>An environment variable by name, ignoring case as Windows does; null when unset or blank.</summary>
    private static string? Variable(IReadOnlyDictionary<string, string> environment, string name)
    {
        var value = environment.TryGetValue(name, out var exact)
            ? exact
            : environment.Where(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Value)
                .FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }
}
