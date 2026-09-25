using System.Globalization;
using System.Text.Json;
using Offramp.Core.Processes;

namespace Offramp.Workspace.Environment;

/// <summary>What <c>dotnet</c> reports about installed and selected SDKs.</summary>
public sealed record DotnetSdkState
{
    /// <summary>False when the <c>dotnet</c> host could not be started.</summary>
    public bool HostFound { get; init; }

    public IReadOnlyList<string> Installed { get; init; } = [];

    /// <summary>The SDK selected in the repository, or null when selection failed.</summary>
    public string? Selected { get; init; }

    /// <summary>Why selection failed (usually a global.json pin), trimmed from dotnet's output.</summary>
    public string? SelectionError { get; init; }
}

/// <summary>Asks the real <c>dotnet</c> host which SDKs exist and which one global.json selects.</summary>
public sealed class DotnetProbe(IProcessRunner runner)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<DotnetSdkState> ProbeAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var list = await runner.RunAsync(
            new ProcessSpec("dotnet", ["--list-sdks"]) { WorkingDirectory = repositoryRoot, Timeout = Timeout },
            cancellationToken);
        if (list.NotFound)
        {
            return new DotnetSdkState { HostFound = false };
        }

        var installed = ParseListSdks(list.StandardOutput);
        var version = await runner.RunAsync(
            new ProcessSpec("dotnet", ["--version"]) { WorkingDirectory = repositoryRoot, Timeout = Timeout },
            cancellationToken);
        if (version.Succeeded)
        {
            return new DotnetSdkState { HostFound = true, Installed = installed, Selected = version.StandardOutput.Trim() };
        }

        var error = FirstMeaningfulLine(version.StandardError) ?? FirstMeaningfulLine(version.StandardOutput);
        return new DotnetSdkState { HostFound = true, Installed = installed, SelectionError = error ?? "dotnet --version failed" };
    }

    /// <summary>Parses <c>dotnet --list-sdks</c> ("10.0.401 [/usr/share/dotnet/sdk]") into sorted versions.</summary>
    public static IReadOnlyList<string> ParseListSdks(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2)[0])
            .Where(v => v.Length > 0 && char.IsAsciiDigit(v[0]))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, SdkVersionComparer.Instance)];

    /// <summary>The major version of an SDK version string ("10.0.401" → 10).</summary>
    public static int? Major(string? version)
    {
        if (version is null)
        {
            return null;
        }

        var dot = version.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? version : version[..dot];
        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    private static string? FirstMeaningfulLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0);
}

/// <summary>Orders SDK versions numerically (9.0.100 &lt; 10.0.100), prereleases before releases.</summary>
public sealed class SdkVersionComparer : IComparer<string>
{
    public static readonly SdkVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        var (xCore, xPre) = Split(x);
        var (yCore, yPre) = Split(y);
        var xs = xCore.Split('.');
        var ys = yCore.Split('.');
        for (var i = 0; i < Math.Max(xs.Length, ys.Length); i++)
        {
            var a = i < xs.Length && int.TryParse(xs[i], CultureInfo.InvariantCulture, out var av) ? av : 0;
            var b = i < ys.Length && int.TryParse(ys[i], CultureInfo.InvariantCulture, out var bv) ? bv : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return (xPre, yPre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(xPre, yPre),
        };
    }

    private static (string Core, string? Pre) Split(string version)
    {
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? (version, null) : (version[..dash], version[(dash + 1)..]);
    }
}

/// <summary>Reads the global.json that governs a directory, if any.</summary>
public static class GlobalJsonReader
{
    public sealed record Found(string AbsolutePath, string? Version, string? RollForward);

    public static Found? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return Read(candidate);
            }
        }

        return null;
    }

    private static Found Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (document.RootElement.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object)
            {
                var version = sdk.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var roll = sdk.TryGetProperty("rollForward", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return new Found(path, version, roll);
            }
        }
        catch (JsonException)
        {
            // dotnet itself reports malformed global.json; doctor surfaces that through SDK selection.
        }

        return new Found(path, null, null);
    }
}
