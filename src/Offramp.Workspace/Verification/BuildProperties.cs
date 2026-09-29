using Offramp.Core.Configuration;

namespace Offramp.Workspace.Verification;

/// <summary>The <c>-p:</c> arguments of every build Offramp runs (<c>scan</c>, verification, <c>csproj modernize</c>).</summary>
public static class BuildProperties
{
    /// <summary>
    /// <c>verify.properties</c>, and outside Windows <c>RestorePackages=false</c> unless they set it: a legacy
    /// project that imports <c>.nuget/NuGet.targets</c> would run <c>NuGet.exe</c> through Mono and fail
    /// (MSB3073), and <c>scan</c> restores <c>packages.config</c> itself
    /// (<c>docs/decisions/0036-legacy-projects-outside-windows.md</c>). Only those targets read the property.
    /// </summary>
    public static IEnumerable<string> Arguments(VerifyConfig config)
    {
        foreach (var (name, value) in config.Properties)
        {
            yield return $"-p:{name}={value}";
        }

        if (!OperatingSystem.IsWindows() && !config.Properties.Keys.Contains("RestorePackages", StringComparer.OrdinalIgnoreCase))
        {
            yield return "-p:RestorePackages=false";
        }
    }
}
