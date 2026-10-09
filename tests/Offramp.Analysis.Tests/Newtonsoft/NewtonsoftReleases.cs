using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Configuration;
using NuGet.Versioning;

namespace Offramp.Analysis.Tests.Newtonsoft;

/// <summary>
/// Runs C# examples against real Newtonsoft.Json releases: each compiled against, and loaded with,
/// one release's net45 assembly in its own collectible load context (ADR 0066).
/// </summary>
internal static class NewtonsoftReleases
{
    /// <summary>Every release from 7.0.1 to the newest 13.x; the test project downloads each one.</summary>
    public static readonly IReadOnlyList<NuGetVersion> All =
    [
        .. new[] { "7.0.1", "8.0.1", "8.0.2", "8.0.3", "9.0.1", "10.0.1", "10.0.2", "10.0.3", "11.0.1", "11.0.2", "12.0.1", "12.0.2", "12.0.3", "13.0.1", "13.0.2", "13.0.3", "13.0.4" }
            .Select(NuGetVersion.Parse),
    ];

    private static readonly Lazy<string> GlobalPackages = new(() =>
        SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(AppContext.BaseDirectory)));

    private static readonly Lazy<IReadOnlyList<MetadataReference>> Platform = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => !Path.GetFileName(p).StartsWith("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase))
            .Select(p => MetadataReference.CreateFromFile(p))]);

    public static string AssemblyPath(NuGetVersion version) =>
        Path.Combine(GlobalPackages.Value, "newtonsoft.json", version.ToNormalizedString(), "lib", "net45", "Newtonsoft.Json.dll");

    /// <summary>What <c>Example.Run()</c> returns under the release, or the compiler's errors.</summary>
    public static string Run(string source, NuGetVersion version)
    {
        var newtonsoft = AssemblyPath(version);
        if (!File.Exists(newtonsoft))
        {
            throw new InvalidOperationException($"Newtonsoft.Json {version} is not in the global packages folder; restore the test project.");
        }

        var compilation = CSharpCompilation.Create(
            "Example" + version.ToNormalizedString().Replace('.', '_'),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            [.. Platform.Value, MetadataReference.CreateFromFile(newtonsoft)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable)
                .WithSpecificDiagnosticOptions([new("CS0618", ReportDiagnostic.Suppress), new("CS0612", ReportDiagnostic.Suppress)]));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
        {
            return "COMPILE: " + string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
        }

        var context = new ReleaseLoadContext(newtonsoft);
        try
        {
            image.Position = 0;
            var example = context.LoadFromStream(image).GetType("Example")
                ?? throw new InvalidOperationException("The example declares no class named Example.");
            var run = example.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Example has no public static Run().");
            return (string)run.Invoke(null, null)!;
        }
        finally
        {
            context.Unload();
        }
    }

    private sealed class ReleaseLoadContext(string newtonsoft) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            string.Equals(name.Name, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase) ? LoadFromAssemblyPath(newtonsoft) : null;
    }
}
