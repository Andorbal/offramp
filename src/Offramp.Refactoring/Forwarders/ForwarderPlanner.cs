using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Moves;
using Offramp.Refactoring.ProjectFiles;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Refactoring.Forwarders;

public sealed record ForwardersRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>The revision to read the source's former public types at; null reads the last scan's compilation.</summary>
    public string? Since { get; init; }

    public required IGitService Git { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>The forwarders to write, and the change set that writes them (null when nothing changes).</summary>
public sealed record ForwardersPlan(ForwardersResult Result, ChangeSet? ChangeSet);

/// <summary>
/// Keeps binary consumers of a source assembly working after its types moved
/// (docs/spec/commands/move.md#forwarders): finds the public types the source declared
/// before (at <c>--since</c>, or in the last scan's compilation) that the destination declares
/// now, writes <c>TypeForwarders.cs</c> in the source, and adds the source's reference to the
/// destination. Strings naming a moved type with the source assembly are reported (<c>OFR2301</c>).
/// </summary>
public static partial class ForwarderPlanner
{
    public const string FileName = "TypeForwarders.cs";

    public const string Command = "forwarders";

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".offramp", ".vs", "bin", "obj", "node_modules", "packages",
    };

    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".config", ".json", ".resx", ".settings", ".xaml", ".xml", ".yaml", ".yml",
    };

    public static async Task<ForwardersPlan?> PlanAsync(ForwardersRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model;
        var bag = request.Diagnostics;
        var source = model.Projects.Single(p => p.Id == request.From);
        var destination = model.Projects.Single(p => p.Id == request.To);
        if (source.Id == destination.Id)
        {
            bag.Report(DiagnosticCatalog.OFR2002, $"The destination is the source project itself ({source.Id}).", new DiagnosticLocation(source.Id));
            return null;
        }

        var former = request.Since is null ? Recorded(request, source) : await AtRevisionAsync(request, source, destination, cancellationToken);
        if (former is null)
        {
            return null;
        }

        var now = OnDisk(request.RepositoryRoot, Folder(destination.Id), null);
        var still = OnDisk(request.RepositoryRoot, Folder(source.Id), Folder(destination.Id));
        var forwarded = former.Keys
            .Where(t => now.ContainsKey(t) && !still.ContainsKey(t))
            .Order(StringComparer.Ordinal)
            .Select(t => new ForwardedType { Type = t, File = now[t] })
            .ToList();
        var assembly = source.AssemblyName ?? source.Name;
        var strings = StringReferences(request.RepositoryRoot, forwarded, assembly, Folder(source.Id) + "/" + FileName);
        foreach (var found in strings)
        {
            bag.Report(DiagnosticCatalog.OFR2301,
                $"Names {found.Type} in {assembly} as a string; forwarders do not redirect strings. It now lives in {destination.AssemblyName ?? destination.Name}.",
                new DiagnosticLocation(null, found.File, found.Line));
        }

        var result = new ForwardersResult
        {
            From = source.Id,
            To = destination.Id,
            Since = request.Since,
            Forwarded = forwarded,
            ProjectEdits = [],
            StringReferences = strings,
        };
        if (forwarded.Count == 0)
        {
            return new ForwardersPlan(result, null);
        }

        var projectBytes = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, source.Id));
        var editor = ProjectFileEditor.Load(projectBytes);
        var reference = MoveChangeSet.Relative(source.Id, destination.Id);
        var edits = new List<ProjectEdit>();
        if (!editor.HasItem("ProjectReference", reference))
        {
            if (MovePlanner.Reach(model, destination.Id).Contains(source.Id))
            {
                bag.Report(DiagnosticCatalog.OFR2001,
                    $"{source.Id} cannot reference {destination.Id}, which depends on it, so it cannot forward to it. Forwarding needs a third assembly both can reference.",
                    new DiagnosticLocation(source.Id));
                return new ForwardersPlan(result, null);
            }

            editor.AddProjectReference(reference);
            edits.Add(new ProjectEdit { Project = source.Id, Kind = ProjectEditKind.AddProjectReference, Value = destination.Id });
        }

        if (!editor.IsSdkStyle && !editor.HasItem("Compile", FileName))
        {
            editor.AddCompile(FileName);
            edits.Add(new ProjectEdit { Project = source.Id, Kind = ProjectEditKind.AddCompile, Value = FileName });
        }

        var file = (Folder(source.Id).Length == 0 ? "" : Folder(source.Id) + "/") + FileName;
        var changeSet = new ChangeSet();
        var path = RepoPaths.ToAbsolute(request.RepositoryRoot, file);
        var newline = Encoding.UTF8.GetString(projectBytes).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (File.Exists(path))
        {
            var before = File.ReadAllBytes(path);
            changeSet.Edit(file, before, Encoding.UTF8.GetBytes(Render(source, destination, Existing(Encoding.UTF8.GetString(before)).Concat(forwarded.Select(f => TypeOf(f.Type))), newline)));
        }
        else
        {
            changeSet.Create(file, Render(source, destination, forwarded.Select(f => TypeOf(f.Type)), newline));
        }

        changeSet.Edit(source.Id, projectBytes, editor.Save());
        return new ForwardersPlan(result with { File = file, ProjectEdits = edits, Preview = changeSet.Preview() }, changeSet);
    }

    /// <summary>Public top-level types declared in the source's recorded compilation, with their files.</summary>
    private static Dictionary<string, string>? Recorded(ForwardersRequest request, ProjectInfo source)
    {
        using var loader = new CompilationLoader(request.RepositoryRoot);
        var compilation = CompilationLoader.PreferredTarget(source) is { } tfm ? loader.LoadForProject(source, tfm) : null;
        if (compilation is null)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0004, $"The compiler log has no compilation for {source.Id}; run `offramp scan` again, or pass --since.");
            return null;
        }

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var file = RepoPaths.ToRepositoryRelative(request.RepositoryRoot, Path.GetFullPath(tree.FilePath));
            if (Inside(file, Folder(source.Id)) && !IsBuildOutput(file[Folder(source.Id).Length..]))
            {
                Collect(types, tree, file);
            }
        }

        return types;
    }

    /// <summary>Public top-level types declared by the source folder's C# files at a revision.</summary>
    private static async Task<Dictionary<string, string>?> AtRevisionAsync(ForwardersRequest request, ProjectInfo source, ProjectInfo destination, CancellationToken cancellationToken)
    {
        var files = await request.Git.ListFilesAsync(request.RepositoryRoot, request.Since!, Folder(source.Id), cancellationToken);
        if (files is null)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR2302, $"'{request.Since}' is not a commit in this repository.",
                data: [KeyValuePair.Create<string, JsonNode?>("since", request.Since)]);
            return null;
        }

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsBuildOutput(file[Folder(source.Id).Length..]) || (Folder(destination.Id).Length > 0 && Inside(file, Folder(destination.Id))))
            {
                continue;
            }

            if (await request.Git.ShowFileAsync(request.RepositoryRoot, request.Since!, file, cancellationToken) is { } text)
            {
                Collect(types, CSharpSyntaxTree.ParseText(text, cancellationToken: cancellationToken), file);
            }
        }

        return types;
    }

    /// <summary>Public top-level types declared by the C# files under a folder now (build output and an excluded folder aside).</summary>
    private static Dictionary<string, string> OnDisk(string root, string folder, string? except)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Files(root, folder).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            if ((except is { Length: > 0 } && Inside(file, except)) || file.EndsWith("/" + FileName, StringComparison.Ordinal))
            {
                continue;
            }

            Collect(types, CSharpSyntaxTree.ParseText(File.ReadAllText(RepoPaths.ToAbsolute(root, file))), file);
        }

        return types;
    }

    /// <summary>Adds the public top-level types (classes, structs, interfaces, enums, records, delegates) a tree declares.</summary>
    private static void Collect(Dictionary<string, string> types, SyntaxTree tree, string file)
    {
        var members = tree.GetRoot()
            .DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax && m.Modifiers.Any(SyntaxKind.PublicKeyword));
        foreach (var member in members)
        {
            var (name, arity) = member switch
            {
                TypeDeclarationSyntax t => (t.Identifier.ValueText, t.TypeParameterList?.Parameters.Count ?? 0),
                DelegateDeclarationSyntax d => (d.Identifier.ValueText, d.TypeParameterList?.Parameters.Count ?? 0),
                BaseTypeDeclarationSyntax b => (b.Identifier.ValueText, 0),
                _ => ("", 0),
            };
            var namespaces = member.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString());
            var metadataName = string.Join('.', namespaces.Append(name)) + (arity > 0 ? "`" + arity.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            types.TryAdd(metadataName, file);
        }
    }

    /// <summary>The <c>typeof</c> operand for a metadata name: <c>global::Ns.Name&lt;,&gt;</c>.</summary>
    private static string TypeOf(string metadataName)
    {
        var tick = metadataName.IndexOf('`', StringComparison.Ordinal);
        if (tick < 0)
        {
            return "global::" + metadataName;
        }

        var arity = int.Parse(metadataName.AsSpan(tick + 1), System.Globalization.CultureInfo.InvariantCulture);
        return "global::" + metadataName[..tick] + "<" + new string(',', arity - 1) + ">";
    }

    /// <summary>The <c>typeof</c> operands of the forwarders an existing file declares.</summary>
    private static IEnumerable<string> Existing(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<AttributeSyntax>()
            .Where(a => a.Name.ToString().EndsWith("TypeForwardedTo", StringComparison.Ordinal) || a.Name.ToString().EndsWith("TypeForwardedToAttribute", StringComparison.Ordinal))
            .Select(a => a.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .OfType<TypeOfExpressionSyntax>()
            .Select(t => t.Type.ToString());

    private static string Render(ProjectInfo source, ProjectInfo destination, IEnumerable<string> types, string newline)
    {
        var builder = new StringBuilder();
        builder.Append("// <auto-generated>").Append(newline);
        builder.Append("//     Written by `offramp forwarders`: types that moved from ").Append(source.AssemblyName ?? source.Name)
            .Append(" to ").Append(destination.AssemblyName ?? destination.Name).Append(newline);
        builder.Append("//     keep resolving for code compiled against ").Append(source.AssemblyName ?? source.Name).Append('.').Append(newline);
        builder.Append("// </auto-generated>").Append(newline).Append(newline);
        foreach (var type in types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            builder.Append("[assembly: global::System.Runtime.CompilerServices.TypeForwardedTo(typeof(").Append(type).Append("))]").Append(newline);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Strings naming a moved type with the source assembly (<c>"Ns.Type, Source"</c>): string
    /// literals in C# files, and lines of configuration and data files.
    /// </summary>
    private static List<StringReference> StringReferences(string root, List<ForwardedType> forwarded, string assembly, string generated)
    {
        var references = new List<StringReference>();
        if (forwarded.Count == 0)
        {
            return references;
        }

        var pattern = new Regex(
            @"(?<![\w.`])(?<type>" + string.Join('|', forwarded.Select(f => Regex.Escape(f.Type)).OrderByDescending(t => t.Length)) + @")(?:\[.*?\])?\s*,\s*" + Regex.Escape(assembly) + @"(?![\w.])",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        foreach (var file in Files(root, ""))
        {
            var extension = Path.GetExtension(file);
            if (file == generated || (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) && !DataExtensions.Contains(extension)))
            {
                continue;
            }

            var text = File.ReadAllText(RepoPaths.ToAbsolute(root, file));
            var lines = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ? StringLines(text) : text.Split('\n').Select((l, i) => (Line: i + 1, Text: l));
            foreach (var (line, content) in lines)
            {
                foreach (Match match in pattern.Matches(content))
                {
                    references.Add(new StringReference { File = file, Line = line, Type = match.Groups["type"].Value, Text = content.Trim() });
                }
            }
        }

        return [.. references.DistinctBy(r => (r.File, r.Line, r.Type))];
    }

    /// <summary>The string literals of a C# file, each with the line it starts on.</summary>
    private static IEnumerable<(int Line, string Text)> StringLines(string text) =>
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantTokens()
            .Where(t => t.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.Utf8StringLiteralToken
                or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken)
            .Select(t => (t.GetLocation().GetLineSpan().StartLinePosition.Line + 1, t.ValueText));

    /// <summary>Files under a repository folder, repository-relative and sorted, skipping build output and tool state.</summary>
    private static List<string> Files(string root, string folder)
    {
        var start = RepoPaths.ToAbsolute(root, folder.Length == 0 ? "." : folder);
        var files = new List<string>();
        if (!Directory.Exists(start))
        {
            return files;
        }

        var pending = new Stack<string>([start]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            files.AddRange(Directory.EnumerateFiles(directory).Select(f => RepoPaths.ToRepositoryRelative(root, f)));
            foreach (var sub in Directory.EnumerateDirectories(directory).Where(d => !SkippedDirectories.Contains(Path.GetFileName(d))))
            {
                pending.Push(sub);
            }
        }

        return [.. files.Order(StringComparer.Ordinal)];
    }

    private static bool IsBuildOutput(string relative) =>
        relative.Split('/').Any(SkippedDirectories.Contains);

    private static bool Inside(string file, string folder) =>
        folder.Length == 0 || file.StartsWith(folder + "/", StringComparison.Ordinal);

    private static string Folder(string project) => project.Contains('/', StringComparison.Ordinal) ? project[..project.LastIndexOf('/')] : "";
}
