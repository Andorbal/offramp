using Offramp.Workspace.Doctor;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Scanning;

/// <summary>A legacy project the compile-only block does not reach, and why (<c>OFR0122</c>).</summary>
/// <param name="Project">Repository-relative project path.</param>
/// <param name="Cause"><c>import-disabled</c>, <c>nearer-props</c>, <c>msbuild-extensions-path</c>, <c>common-props</c>, or <c>reset</c>.</param>
/// <param name="Reason">What happened, and what to change.</param>
/// <param name="File">The repository-relative file to change, when known.</param>
internal sealed record CompileOnlyGap(string Project, string Cause, string Reason, string? File);

/// <summary>
/// Outside Windows, the compile-only block in the root <c>Directory.Build.props</c> gives legacy projects their
/// reference assemblies; a project it does not reach fails with MSB3644 and nothing else says why. Open Live Writer's
/// shared <c>writer.build.settings</c> sets <c>MSBuildExtensionsPath</c>, so no project ever imported
/// <c>Microsoft.Common.props</c>, which imports <c>Directory.Build.props</c>.
/// </summary>
internal static class CompileOnlyReach
{
    /// <summary>
    /// The legacy projects whose evaluations lack <c>OfframpCompileOnly</c> while the root <c>Directory.Build.props</c>
    /// has the block, by project. Only meaningful for a build that ran here, outside Windows.
    /// </summary>
    public static IReadOnlyList<CompileOnlyGap> Find(string root, IEnumerable<(string Project, IReadOnlyList<EvaluatedProject> Evaluations)> projects, CapturePathMapper mapper)
    {
        var props = Path.Combine(root, CompileOnlyConditional.FileName);
        if (!File.Exists(props) || !File.ReadAllText(props).Contains(CompileOnlyConditional.Marker, StringComparison.Ordinal))
        {
            return [];
        }

        var gaps = new List<CompileOnlyGap>();
        foreach (var (project, evaluations) in projects.OrderBy(p => p.Project, StringComparer.Ordinal))
        {
            // Native projects (.vcxproj) import shared settings too, but the block is for .NET projects.
            if (Path.GetExtension(project).ToLowerInvariant() is not (".csproj" or ".vbproj" or ".fsproj")
                || evaluations.Count == 0
                || evaluations.Any(e => e.IsTrue("UsingMicrosoftNETSdk") || e.IsTrue("OfframpCompileOnly")))
            {
                continue;
            }

            var (cause, reason, file) = Cause(evaluations[0], Path.GetFullPath(props), mapper);
            gaps.Add(new CompileOnlyGap(project, cause, reason, file));
        }

        return gaps;
    }

    private static (string Cause, string Reason, string? File) Cause(EvaluatedProject evaluation, string rootProps, CapturePathMapper mapper)
    {
        if (string.Equals(evaluation.Property("ImportDirectoryBuildProps"), "false", StringComparison.OrdinalIgnoreCase))
        {
            var file = FirstSetting(evaluation, "ImportDirectoryBuildProps", mapper);
            return ("import-disabled", $"{file ?? "the project"} sets ImportDirectoryBuildProps to false, so no Directory.Build.props is imported", file);
        }

        var used = evaluation.Property("DirectoryBuildPropsPath");
        if (used is not null && mapper.ToLocal(used) is { } local && !string.Equals(Path.GetFullPath(local), rootProps, StringComparison.Ordinal))
        {
            var nearer = mapper.ToRelative(used)!;
            return ("nearer-props", $"MSBuild imports the nearer {nearer}, which does not import the root one; import it there with <Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />", nearer);
        }

        if (!evaluation.IsTrue("MicrosoftCommonPropsHasBeenImported"))
        {
            return FirstSetting(evaluation, "MSBuildExtensionsPath", mapper) is { } file
                ? ("msbuild-extensions-path", $"{file} sets MSBuildExtensionsPath, so MSBuild never imports Microsoft.Common.props, which imports Directory.Build.props; condition that property on '$(OS)' == 'Windows_NT'", file)
                : ("common-props", "the project never imports Microsoft.Common.props, which imports Directory.Build.props", null);
        }

        return ("reset", "Directory.Build.props is imported, but a later file sets OfframpCompileOnly to something else", null);
    }

    /// <summary>The first file inside the repository, in import order (the project file first), that sets the property.</summary>
    private static string? FirstSetting(EvaluatedProject evaluation, string property, CapturePathMapper mapper)
    {
        foreach (var file in new[] { evaluation.ProjectFile }.Concat(evaluation.Imports))
        {
            if (mapper.ToRelative(file) is { } relative
                && ProjectFileChecks.Load(mapper.ToLocal(file)!) is { Root: { } project }
                && project.Descendants().Any(e => e.Name.LocalName == property && e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                return relative;
            }
        }

        return null;
    }
}
