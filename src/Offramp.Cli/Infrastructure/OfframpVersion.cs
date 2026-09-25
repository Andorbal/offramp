using System.Reflection;

namespace Offramp.Cli.Infrastructure;

/// <summary>The tool version, from the git tag via MinVer (no build metadata).</summary>
public static class OfframpVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(OfframpVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
