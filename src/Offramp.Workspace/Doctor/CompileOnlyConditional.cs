using System.Text;

namespace Offramp.Workspace.Doctor;

/// <summary>What <c>doctor --fix</c> would do, or did, to Directory.Build.props.</summary>
public sealed record CompileOnlyFix
{
    /// <summary>Repository-relative path of the props file.</summary>
    public required string File { get; init; }

    /// <summary>True when the block is already there; nothing to do.</summary>
    public required bool AlreadyPresent { get; init; }

    /// <summary>True when the file was written in this run.</summary>
    public required bool Applied { get; init; }

    /// <summary>The unified diff of the change, or null when nothing changes.</summary>
    public string? Diff { get; init; }
}

/// <summary>
/// The compile-only conditional of docs/compiling-on-macos.md, inserted into the
/// repository's root Directory.Build.props as text, so every other byte of the
/// file (formatting, comments, line endings) stays as it was. It has three sections,
/// each found by its own marker, so a file with the first sections from an earlier
/// Offramp gains only the sections it lacks.
/// </summary>
public static class CompileOnlyConditional
{
    public const string FileName = "Directory.Build.props";
    public const string Marker = "<OfframpCompileOnly>";

    /// <summary>Marks the ASP.NET web targets section.</summary>
    public const string WebTargetsMarker = "\"" + Model.WindowsOnlyBuildSteps.WebTargetsPackage + "\"";

    /// <summary>The package version the web targets section references; the last one published.</summary>
    public const string WebTargetsVersion = "14.0.0.3";

    public static readonly string[] CompileOnlyLines =
    [
        "<!-- Compile-only builds on macOS/Linux: skip steps that need Windows (added by offramp doctor). -->",
        "<PropertyGroup Condition=\"!$([MSBuild]::IsOSPlatform('Windows'))\">",
        "  <GenerateSerializationAssemblies>Off</GenerateSerializationAssemblies>",
        "  <EnableWindowsTargeting>true</EnableWindowsTargeting>",
        "  <OfframpCompileOnly>true</OfframpCompileOnly>",
        "</PropertyGroup>",
    ];

    /// <summary>
    /// ASP.NET (System.Web) projects import <c>$(VSToolsPath)/WebApplications/Microsoft.WebApplication.targets</c>,
    /// which only Visual Studio installs. Outside Windows the targets come from a package whose props set
    /// <c>VSToolsPath</c>; the reference is implicit, so it stays out of package analysis and needs no
    /// <c>PackageVersion</c> under central package management. <c>MvcBuildViews</c> would run <c>AspNetCompiler</c>,
    /// and the Web Deploy targets (imported from <c>$(AspNetTargetsPath)</c> when they exist there) hook tasks built
    /// for .NET Framework's MSBuild into <c>Clean</c>; neither runs on .NET's MSBuild. Only SDK-style projects with a
    /// <c>VSToolsPath</c> get the package.
    /// </summary>
    public static readonly string[] WebTargetsLines =
    [
        "<!-- ASP.NET (System.Web) projects on macOS/Linux: the Visual Studio web targets come from a package; views are not precompiled and Web Deploy publishing is left out (added by offramp doctor). -->",
        "<PropertyGroup Condition=\"!$([MSBuild]::IsOSPlatform('Windows'))\">",
        "  <MvcBuildViews>false</MvcBuildViews>",
        "  <AspNetTargetsPath>$(MSBuildThisFileDirectory)</AspNetTargetsPath>",
        "</PropertyGroup>",
        "<ItemGroup Condition=\"!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' == 'true' And '$(VSToolsPath)' != ''\">",
        "  <PackageReference Include=" + WebTargetsMarker + " Version=\"" + WebTargetsVersion + "\" IsImplicitlyDefined=\"true\" PrivateAssets=\"all\" />",
        "</ItemGroup>",
    ];

    /// <summary>Marks the legacy projects section.</summary>
    public const string LegacyMarker = "<OfframpLegacyPackages>";

    /// <summary>The reference assemblies package version the legacy section references.</summary>
    public const string ReferenceAssembliesVersion = "1.0.3";

    /// <summary>
    /// Legacy (non-SDK) projects get neither the .NET Framework reference assemblies nor the web targets
    /// from the SDK (docs/decisions/0037-legacy-projects-outside-windows.md). Restored as
    /// <c>PackageReference</c> projects, they take both from packages, as SDK-style projects do; their
    /// <c>packages.config</c> is still ignored by <c>dotnet restore</c>, and <c>offramp scan</c> restores it.
    /// The reference assemblies package wires Visual Basic's runtime for SDK-style projects only, so a
    /// legacy Visual Basic project references <c>Microsoft.VisualBasic</c> and passes it to the compiler
    /// the way the SDK does, instead of looking for it in a .NET Framework directory that is not there.
    /// </summary>
    public static readonly string[] LegacyLines =
    [
        "<!-- Legacy (non-SDK) projects on macOS/Linux: the .NET Framework reference assemblies and the web targets come from packages, as for SDK-style projects; offramp scan restores packages.config (added by offramp doctor). -->",
        "<PropertyGroup Condition=\"!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' != 'true'\">",
        "  <RestoreProjectStyle>PackageReference</RestoreProjectStyle>",
        "  <OfframpLegacyPackages>true</OfframpLegacyPackages>",
        "</PropertyGroup>",
        "<ItemGroup Condition=\"!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' != 'true'\">",
        "  <PackageReference Include=\"Microsoft.NETFramework.ReferenceAssemblies\" Version=\"" + ReferenceAssembliesVersion + "\" IsImplicitlyDefined=\"true\" PrivateAssets=\"all\" />",
        "  <PackageReference Include=" + WebTargetsMarker + " Version=\"" + WebTargetsVersion + "\" IsImplicitlyDefined=\"true\" PrivateAssets=\"all\" />",
        "  <Reference Include=\"Microsoft.VisualBasic\" Condition=\"'$(MSBuildProjectExtension)' == '.vbproj'\" />",
        "</ItemGroup>",
        "<Target Name=\"OfframpLegacyVisualBasicRuntime\" BeforeTargets=\"CoreCompile\" Condition=\"!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' != 'true' And '$(Language)' == 'VB' And '$(VBRuntime)' == ''\">",
        "  <PropertyGroup>",
        "    <VBRuntime Condition=\"'%(ReferencePath.FileName)' == 'Microsoft.VisualBasic'\">%(ReferencePath.Identity)</VBRuntime>",
        "    <DisableSdkPath>true</DisableSdkPath>",
        "  </PropertyGroup>",
        "</Target>",
    ];

    /// <summary>All three sections, as a new file gets them.</summary>
    public static IReadOnlyList<string> BlockLines => [.. CompileOnlyLines, .. WebTargetsLines, .. LegacyLines];

    /// <summary>True when the legacy projects section is in <paramref name="content"/>.</summary>
    public static bool HasLegacySection(string? content) => content is not null && content.Contains(LegacyMarker, StringComparison.Ordinal);

    /// <summary>True when all three sections are in <paramref name="content"/>.</summary>
    public static bool IsPresent(string? content) => content is not null && MissingLines(content).Count == 0;

    /// <summary>The new content for <paramref name="current"/> (null when the file does not exist), or null when already present.</summary>
    public static string? Apply(string? current)
    {
        if (current is null)
        {
            var created = new StringBuilder("<Project>\n");
            foreach (var line in BlockLines)
            {
                created.Append("  ").Append(line).Append('\n');
            }

            return created.Append("</Project>\n").ToString();
        }

        var lines = MissingLines(current);
        if (lines.Count == 0)
        {
            return null;
        }

        var newline = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var close = current.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (close < 0)
        {
            throw new InvalidDataException($"{FileName} has no closing </Project> element.");
        }

        var lineStart = current.LastIndexOf('\n', Math.Max(close - 1, 0)) + 1;
        var closingIndent = current[lineStart..close];
        var indent = closingIndent.Trim().Length == 0 ? closingIndent + "  " : "  ";
        var insertion = new StringBuilder();
        if (lineStart != close || close == 0)
        {
            // "</Project>" shares its line with other content; start the block on a new line.
            insertion.Append(newline);
        }

        foreach (var line in lines)
        {
            insertion.Append(indent).Append(line).Append(newline);
        }

        var insertAt = closingIndent.Trim().Length == 0 ? lineStart : close;
        if (insertAt == close && lineStart != close)
        {
            insertion.Append(closingIndent.Trim().Length == 0 ? closingIndent : "");
        }

        return current[..insertAt] + insertion + current[insertAt..];
    }

    private static int Occurrences(string content, string value)
    {
        var count = 0;
        for (var at = content.IndexOf(value, StringComparison.OrdinalIgnoreCase); at >= 0; at = content.IndexOf(value, at + value.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static List<string> MissingLines(string content)
    {
        var lines = new List<string>();
        if (!content.Contains(Marker, StringComparison.Ordinal))
        {
            lines.AddRange(CompileOnlyLines);
        }

        // The legacy section names the web targets package too; the web section is there when
        // the package is named once more than the legacy section accounts for.
        var legacy = content.Contains(LegacyMarker, StringComparison.Ordinal);
        if (Occurrences(content, WebTargetsMarker) <= (legacy ? 1 : 0))
        {
            lines.AddRange(WebTargetsLines);
        }

        if (!legacy)
        {
            lines.AddRange(LegacyLines);
        }

        return lines;
    }
}
