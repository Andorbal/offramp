using System.Security;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Compilations;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace.Model;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Refactoring.Moves;

/// <summary>What <c>move extract</c> is asked to do.</summary>
public sealed record MoveExtractRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required string WorkspaceHash { get; init; }

    /// <summary>The source project's id.</summary>
    public required string From { get; init; }

    /// <summary><c>--types</c>: type names, fully qualified or simple when unique.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary><c>--files</c>: repository-relative patterns (the command anchors them at the working directory, as <c>move plan</c> does).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary><c>--new</c>: the new project's name (its file is <c>NAME.csproj</c>).</summary>
    public required string NewName { get; init; }

    /// <summary><c>--tfm</c>; empty uses the source's target frameworks.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary><c>--dir</c>, repository-relative; null puts the project next to the source's folder.</summary>
    public string? Directory { get; init; }

    public required TargetReferenceResolver References { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>A requested type and the files that declare it.</summary>
public sealed record ExtractedType(string Type, IReadOnlyList<string> Files);

/// <summary>The <c>result</c> of <c>offramp move extract</c> (<c>schemas/v1/move-extract.json</c>).</summary>
public sealed record MoveExtractResult
{
    public required string From { get; init; }

    /// <summary>The new project file, repository-relative.</summary>
    public required string NewProject { get; init; }

    public required IReadOnlyList<string> TargetFrameworks { get; init; }

    /// <summary><c>--types</c>, resolved.</summary>
    public IReadOnlyList<ExtractedType> Types { get; init; } = [];

    /// <summary>The files asked for (from the types and the patterns); the plan adds what they need.</summary>
    public required IReadOnlyList<string> Requested { get; init; }

    /// <summary>The new project file as the template makes it, before the move's edits.</summary>
    public required string ProjectFile { get; init; }

    public required MovePlanDocument Plan { get; init; }

    /// <summary>The unified diff of the new and edited project files and the renames; null once applied.</summary>
    public string? Preview { get; init; }

    /// <summary>With <c>--apply</c>: what <c>move apply</c> did.</summary>
    public MoveApplyResult? Apply { get; init; }
}

/// <summary>The dry run, and what applying it needs.</summary>
public sealed record MoveExtractPlan(MoveExtractResult Result, WorkspaceModel Model, IReadOnlyDictionary<string, byte[]> Created);

/// <summary>
/// <c>move extract</c> (docs/spec/commands/move.md#move-extract): creates a project from a
/// template (SDK-style, the source's language settings, analyzers, and .NET Framework
/// references) and plans the move of the requested files into it with <see cref="MovePlanner"/>.
/// The new project does not exist while planning: its compilation per target is built in
/// memory, from the source's recorded compilation for a target the source has and from
/// the resolved reference assemblies for another. <c>move apply</c> then creates it with
/// the move's edits, in one journal.
/// </summary>
public static class MoveExtractor
{
    private static readonly string[] CopiedProperties = ["LangVersion", "Nullable", "ImplicitUsings"];

    public static async Task<MoveExtractPlan?> PlanAsync(MoveExtractRequest request, CancellationToken cancellationToken)
    {
        var bag = request.Diagnostics;
        var source = request.Model.Projects.Single(p => p.Id == request.From);
        if (source.Language != "csharp")
        {
            bag.Report(DiagnosticCatalog.OFR2205, "Moves analyze C# projects only.", new DiagnosticLocation(source.Id));
            return null;
        }

        var directory = RepoPaths.Normalize(request.Directory ?? Sibling(source.Id, request.NewName)).TrimEnd('/');
        var projectPath = $"{directory}/{request.NewName}.csproj";
        var absolute = RepoPaths.ToAbsolute(request.RepositoryRoot, directory);
        if (request.Model.Projects.Any(p => p.Id == projectPath) || (System.IO.Directory.Exists(absolute) && System.IO.Directory.EnumerateFileSystemEntries(absolute).Any()))
        {
            bag.Report(DiagnosticCatalog.OFR2007, $"{directory} already exists; move extract creates its project in a new folder.", new DiagnosticLocation(source.Id, directory));
            return null;
        }

        using var loader = new CompilationLoader(request.RepositoryRoot);
        var sourceTarget = CompilationLoader.PreferredTarget(source);
        if (sourceTarget is null || loader.LoadForProject(source, sourceTarget) is not CSharpCompilation compilation)
        {
            bag.Report(DiagnosticCatalog.OFR0004, $"The compiler log has no compilation for {source.Id}; run `offramp scan` again.");
            return null;
        }

        var types = Types(request, source, compilation);
        var files = Files(request, source);
        if (types is null || files is null)
        {
            return null;
        }

        var requested = types.SelectMany(t => t.Files).Concat(files).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var tfms = request.TargetFrameworks.Count > 0 ? request.TargetFrameworks : source.TargetFrameworks;
        var content = Template(source, tfms);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        var info = new ProjectInfo
        {
            Id = projectPath,
            Name = request.NewName,
            AssemblyName = request.NewName,
            RootNamespace = source.RootNamespace ?? source.Name,
            SdkStyle = true,
            Sdk = "Microsoft.NET.Sdk",
            TargetFrameworks = tfms,
            Properties = new SortedDictionary<string, string>(
                source.Properties.Where(p => p.Key == "ManagePackageVersionsCentrally" || CopiedProperties.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal),
        };

        var compilations = new List<(string Tfm, CSharpCompilation Compilation)>();
        foreach (var tfm in tfms)
        {
            if (await CompilationAsync(request, source, loader, compilation, info, tfm, cancellationToken).ConfigureAwait(false) is not { } destination)
            {
                return null;
            }

            compilations.Add((tfm, destination));
        }

        // The new project is in the model from here on, so project edits see its settings (central package versions).
        var model = request.Model with { Projects = [.. request.Model.Projects, info] };
        var planned = MovePlanner.Plan(new MovePlanRequest
        {
            RepositoryRoot = request.RepositoryRoot,
            Model = model,
            Config = request.Config,
            WorkspaceHash = request.WorkspaceHash,
            From = source.Id,
            To = projectPath,
            Files = requested,
            CoMove = request.Config.Move.CoMove,
            NamespaceMismatch = request.Config.Move.NamespaceMismatch,
            Diagnostics = bag,
            Create = new NewProject(info, bytes, compilations),
        });
        if (planned is null)
        {
            return null;
        }

        var created = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [projectPath] = bytes };
        var changeSet = MoveChangeSet.Build(request.RepositoryRoot, planned.Plan, new HashSet<string>(StringComparer.Ordinal), out _, model, created);
        await MoveApplier.AddToSolutionsAsync(request.RepositoryRoot, planned.Plan, changeSet, cancellationToken).ConfigureAwait(false);
        var result = new MoveExtractResult
        {
            From = source.Id,
            NewProject = projectPath,
            TargetFrameworks = tfms,
            Types = types,
            Requested = requested,
            ProjectFile = content,
            Plan = planned.Plan,
            Preview = changeSet.Preview(),
        };
        return new MoveExtractPlan(result, model, created);
    }

    /// <summary>The default folder: beside the source project's folder.</summary>
    private static string Sibling(string sourceProject, string name)
    {
        var folder = RepoPaths.Normalize(Path.GetDirectoryName(sourceProject) ?? "");
        var parent = folder.Contains('/', StringComparison.Ordinal) ? folder[..folder.LastIndexOf('/')] : "";
        return parent.Length == 0 ? name : parent + "/" + name;
    }

    /// <summary>Each requested type and its files; null (with OFR2006) when a name matches no type or several.</summary>
    private static List<ExtractedType>? Types(MoveExtractRequest request, ProjectInfo source, CSharpCompilation compilation)
    {
        var declared = compilation.SyntaxTrees
            .Select(t => (Tree: t, File: RepoPaths.ToRepositoryRelative(request.RepositoryRoot, t.FilePath)))
            .Where(t => source.Compile.Contains(t.File, StringComparer.Ordinal))
            .SelectMany(t => t.Tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                .Select(d => compilation.GetSemanticModel(t.Tree).GetDeclaredSymbol(d))
                .OfType<INamedTypeSymbol>())
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToList();
        var result = new List<ExtractedType>();
        var failed = false;
        foreach (var name in request.Types)
        {
            var matches = declared.Where(t => t.ToDisplayString() == name).ToList();
            if (matches.Count == 0)
            {
                matches = [.. declared.Where(t => t.Name == name)];
            }

            if (matches.Count != 1)
            {
                var candidates = matches.Select(t => t.ToDisplayString()).Order(StringComparer.Ordinal).ToList();
                request.Diagnostics.Report(DiagnosticCatalog.OFR2006, candidates.Count == 0
                    ? $"No type named {name} is declared in {source.Id}."
                    : $"{name} names {candidates.Count} types in {source.Id}: {string.Join(", ", candidates)}; name one fully qualified.", new DiagnosticLocation(source.Id));
                failed = true;
                continue;
            }

            var files = matches[0].DeclaringSyntaxReferences
                .Select(r => RepoPaths.ToRepositoryRelative(request.RepositoryRoot, r.SyntaxTree.FilePath))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            result.Add(new ExtractedType(matches[0].ToDisplayString(), files));
        }

        return failed ? null : [.. result.OrderBy(t => t.Type, StringComparer.Ordinal)];
    }

    /// <summary>The source's compiled files each pattern matches; null (with OFR2006) when one matches nothing.</summary>
    private static List<string>? Files(MoveExtractRequest request, ProjectInfo source)
    {
        var result = new List<string>();
        var failed = false;
        foreach (var pattern in request.Files)
        {
            var globs = new PathGlobs([pattern]);
            var matched = source.Compile.Where(globs.Matches).ToList();
            if (matched.Count == 0)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR2006, $"No file {source.Id} compiles matches {pattern}.", new DiagnosticLocation(source.Id));
                failed = true;
            }

            result.AddRange(matched);
        }

        return failed ? null : result;
    }

    /// <summary>The new project file: SDK-style, the source's language settings, analyzers, and .NET Framework references.</summary>
    internal static string Template(ProjectInfo source, IReadOnlyList<string> tfms)
    {
        var builder = new StringBuilder();
        builder.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n");
        builder.Append(tfms.Count == 1 ? $"    <TargetFramework>{tfms[0]}</TargetFramework>\n" : $"    <TargetFrameworks>{string.Join(';', tfms)}</TargetFrameworks>\n");
        builder.Append("    <RootNamespace>").Append(Escape(source.RootNamespace ?? source.Name)).Append("</RootNamespace>\n");
        foreach (var name in CopiedProperties.Where(source.Properties.ContainsKey))
        {
            builder.Append("    <").Append(name).Append('>').Append(Escape(source.Properties[name])).Append("</").Append(name).Append(">\n");
        }

        builder.Append("  </PropertyGroup>\n");
        var central = source.Properties.TryGetValue("ManagePackageVersionsCentrally", out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        var analyzers = source.PackageReferences
            .Where(p => string.Equals(p.PrivateAssets, "all", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (analyzers.Count > 0)
        {
            builder.Append("\n  <ItemGroup>\n");
            foreach (var package in analyzers)
            {
                builder.Append("    <PackageReference Include=\"").Append(Escape(package.Id)).Append('"');
                if (!central && (package.VersionOverride ?? package.Version) is { } version)
                {
                    builder.Append(" Version=\"").Append(Escape(version)).Append('"');
                }

                builder.Append(" PrivateAssets=\"all\" />\n");
            }

            builder.Append("  </ItemGroup>\n");
        }

        var framework = source.AssemblyReferences.Where(r => r.Kind == AssemblyReferenceKind.Framework).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (framework.Count > 0 && tfms.Any(IsNetFramework))
        {
            builder.Append("\n  <ItemGroup Condition=\"'$(TargetFrameworkIdentifier)' == '.NETFramework'\">\n");
            foreach (var name in framework)
            {
                builder.Append("    <Reference Include=\"").Append(Escape(name)).Append("\" />\n");
            }

            builder.Append("  </ItemGroup>\n");
        }

        builder.Append("\n</Project>\n");
        return builder.ToString();
    }

    /// <summary>
    /// The new project's compilation for a target, empty: the source's .NET Framework and SDK
    /// references when the source compiles for the target, else the target's reference assemblies.
    /// </summary>
    private static async Task<CSharpCompilation?> CompilationAsync(MoveExtractRequest request, ProjectInfo source, CompilationLoader loader, CSharpCompilation preferred,
        ProjectInfo info, string tfm, CancellationToken cancellationToken)
    {
        var options = preferred.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary);
        if (source.CompilerCalls.ContainsKey(tfm) && loader.LoadForProject(source, tfm) is CSharpCompilation same)
        {
            var packages = PackageIndex.For(request.RepositoryRoot, source);
            var projects = request.Model.Projects.Select(p => p.AssemblyName ?? p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var framework = same.References.Where(r => same.GetAssemblyOrModuleSymbol(r) is IAssemblySymbol assembly
                && !projects.Contains(assembly.Identity.Name) && packages.Find(assembly.Identity.Name) is null);
            return CSharpCompilation.Create(info.AssemblyName, GlobalUsings(same), framework, same.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary));
        }

        var resolved = await request.References.ResolveAsync(new TargetReferenceRequest { TargetFramework = tfm }, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is { } error)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR2008, $"{info.Id} cannot be compiled for {tfm}: its references did not resolve ({error}).", new DiagnosticLocation(source.Id));
            return null;
        }

        return CSharpCompilation.Create(info.AssemblyName, [], resolved.Paths.Select(p => MetadataReference.CreateFromFile(p)), options);
    }

    /// <summary>The SDK's generated global usings, which an SDK-style project with the same settings also gets.</summary>
    private static IEnumerable<SyntaxTree> GlobalUsings(CSharpCompilation compilation) =>
        compilation.SyntaxTrees.Where(t => t.FilePath.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase));

    private static bool IsNetFramework(string tfm) =>
        tfm.StartsWith("net4", StringComparison.OrdinalIgnoreCase) || tfm.StartsWith("net3", StringComparison.OrdinalIgnoreCase) || tfm.StartsWith("net2", StringComparison.OrdinalIgnoreCase);

    private static string Escape(string value) => SecurityElement.Escape(value) ?? value;
}
