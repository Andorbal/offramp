using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Audits;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding.Text;

namespace Offramp.Scaffolding.Remote;

/// <summary>
/// <c>offramp remote</c> (docs/spec/commands/seams.md#remote): audits the interface's members
/// for the wire, chooses the host framework, and writes the contracts, host, and client projects
/// (and container assets) as new files. Nothing existing is edited.
/// </summary>
public static partial class RemoteGenerator
{
    public static async Task<RemotePlan?> PlanAsync(RemoteRequest request, CSharpCompilation compilation, CancellationToken cancellationToken)
    {
        var location = new DiagnosticLocation(request.Project.Id);
        if (Find(compilation, request.Interface) is not { TypeKind: TypeKind.Interface } contract || !InSource(compilation, contract))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4023, $"'{request.Interface}' is not an interface declared in {request.Project.Id}.", location);
            return null;
        }

        if (Implementation(request, compilation, contract) is not { } implementation)
        {
            return null;
        }

        var memberNames = contract.GetMembers().Concat(contract.AllInterfaces.SelectMany(i => i.GetMembers())).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in request.SkipMembers.Where(s => !memberNames.Contains(s)).Order(StringComparer.Ordinal))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4023, $"--skip-member {unknown}: {contract.Name} has no member named {unknown}.", location);
        }

        if (request.SkipMembers.Any(s => !memberNames.Contains(s)))
        {
            return null;
        }

        var root = request.RepositoryRoot;
        var defaults = RemoteLayout.Defaults(request.Project.Id, request.Project.Name);
        var interfaceTree = contract.DeclaringSyntaxReferences[0].SyntaxTree;
        var layout = new RemoteLayout
        {
            Contract = contract,
            Implementation = implementation,
            ProjectFile = request.Project.Id,
            ContractsDirectory = Directory(request.ContractsDirectory) ?? defaults.Contracts,
            ClientDirectory = Directory(request.ClientDirectory) ?? defaults.Client,
            HostDirectory = Directory(request.HostDirectory) ?? defaults.Host,
            NewLine = interfaceTree.GetText(cancellationToken).ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
        };
        layout = layout with { CentralPackages = CentralPackages(root, layout.HostDirectory) };

        var wire = new WireTypes(layout.ContractsNamespace, layout.Stem + "Mapping");
        var members = RemoteBoundary.Audit(contract, request.SkipMembers, wire);
        var result = new RemoteResult
        {
            Project = request.Project.Id,
            Interface = AuditEngine.Name(contract),
            Implementation = AuditEngine.Name(implementation),
            Serializer = request.Serializer,
            Members = [.. members.Select(m => Describe(m, layout))],
        };

        foreach (var blocked in members.Where(m => m.Status == "blocked"))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4002,
                $"{contract.Name}.{blocked.Symbol.Name} cannot cross the wire ({string.Join("; ", blocked.Problems)}); change it to exchange data, or leave it local with --skip-member {blocked.Symbol.Name}.",
                location, [KeyValuePair.Create<string, JsonNode?>("member", blocked.Symbol.Name)], Severity.Error);
        }

        if (members.Any(m => m.Status == "blocked"))
        {
            return new RemotePlan(result, null);
        }

        foreach (var directory in new[] { layout.ContractsDirectory, layout.ClientDirectory, layout.HostDirectory })
        {
            var path = RepoPaths.ToAbsolute(root, directory);
            if (System.IO.Directory.Exists(path) && System.IO.Directory.EnumerateFileSystemEntries(path).Any())
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR4021, $"{directory} already exists; nothing was generated.", new DiagnosticLocation(request.Project.Id, directory));
            }
        }

        if (request.Diagnostics.HasErrors)
        {
            return new RemotePlan(result, null);
        }

        wire.AssignNames();
        foreach (var member in members.Where(m => m is { Status: "remote", Async: false }))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4020,
                $"{contract.Name}.{member.Symbol.Name} is synchronous: the client blocks on the HTTP call.{(request.AsyncVariant ? $" {contract.Name}Async.{member.AsyncName} does not." : " Generate the asynchronous variant with --async-variant.")}",
                location, [KeyValuePair.Create<string, JsonNode?>("member", member.Symbol.Name)]);
        }

        var sources = HostFramework.Closure(compilation, implementation, contract);
        var (modern, reason, trial) = await ChooseHostAsync(request, compilation, sources, cancellationToken).ConfigureAwait(false);
        if (!modern && request.HostFramework == "auto")
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4022, $"The host is the net48 fallback: {reason}.", location);
        }
        else if (modern && !trial.Compiles && request.HostFramework == "net10-windows")
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4022, $"Generating the net10.0-windows host as asked, but it may not build: {trial.Reason}.", location, severity: Severity.Warning);
        }

        var clientFrameworks = request.Project.TargetFrameworks.Count > 0 ? request.Project.TargetFrameworks : ["net48"];
        var files = new List<(string Path, string Text)>();
        files.AddRange(ContractsTemplate.Files(layout, members, wire));
        files.AddRange(ClientTemplate.Files(layout, members, wire, clientFrameworks, request.Serializer, request.AsyncVariant));
        files.AddRange(modern ? HostTemplate.Modern(layout, members, wire, trial, root) : HostTemplate.Legacy(layout, members, wire));
        if (request.Container)
        {
            files.AddRange(ContainerTemplate.Files(layout, modern));
        }

        var changeSet = new ChangeSet();
        foreach (var (path, text) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            changeSet.Create(path, layout.NewLine == "\n" ? text : text.Replace("\n", layout.NewLine, StringComparison.Ordinal));
        }

        var hostFramework = modern ? HostFramework.Modern : HostFramework.Legacy;
        result = result with
        {
            HostFramework = hostFramework,
            HostFrameworkReason = reason,
            Dtos = [.. wire.Dtos.Select(d => new RemoteDto(AuditEngine.Name(d.Type), $"{layout.ContractsNamespace}.{d.Dto}"))],
            Projects =
            [
                new GeneratedProject("contracts", layout.ContractsProject, ["netstandard2.0"]),
                new GeneratedProject("client", layout.ClientProject, clientFrameworks),
                new GeneratedProject("host", layout.HostProject, [hostFramework]),
            ],
            LinkedSources = modern ? [.. trial.Sources.Select(t => File(root, t))] : [],
            Files = [.. files.Select(f => f.Path).Order(StringComparer.Ordinal)],
            Registration = ClientTemplate.RegistrationCall(layout),
            NextSteps = NextSteps(request, layout),
            Preview = changeSet.Preview(),
        };
        return new RemotePlan(result, changeSet);
    }

    private static async Task<(bool Modern, string Reason, HostTrial Trial)> ChooseHostAsync(
        RemoteRequest request, CSharpCompilation compilation, IReadOnlyList<SyntaxTree> sources, CancellationToken cancellationToken)
    {
        if (request.HostFramework == "net48")
        {
            return (false, "--host-framework net48", new HostTrial(false, "not checked", sources, []));
        }

        var trial = await HostFramework.TryModernAsync(request.Project, compilation, sources, request.References, cancellationToken).ConfigureAwait(false);
        if (request.HostFramework == "net10-windows")
        {
            var packages = trial.Packages.Count > 0 ? trial.Packages : [("Microsoft.Windows.Compatibility", ScaffoldPackages.Version("Microsoft.Windows.Compatibility"))];
            return (true, trial.Compiles ? trial.Reason : "--host-framework net10-windows", trial with { Packages = packages });
        }

        return (trial.Compiles, trial.Reason, trial);
    }

    private static List<string> NextSteps(RemoteRequest request, RemoteLayout layout)
    {
        var steps = new List<string>
        {
            request.Solution is { } solution
                ? $"dotnet sln {solution} add {layout.ContractsProject} {layout.ClientProject} {layout.HostProject}"
                : $"Add {layout.ContractsProject}, {layout.ClientProject}, and {layout.HostProject} to the solution.",
            $"Reference {layout.ClientProject} from the application that registers services and call {ClientTemplate.RegistrationCall(layout)}",
            $"Set Remote:{layout.Stem}:Mode to remote and Remote:{layout.Stem}:Url to the host's address to call the host; anything else keeps the local implementation.",
        };
        if (request.Container)
        {
            steps.Add($"docker build -f {layout.HostDirectory}/Dockerfile -t {CSharpText.Kebab(layout.HostName)} . (on a Windows container host), then kubectl apply -f {layout.HostDirectory}/kubernetes.yaml");
        }

        return steps;
    }

    private static RemoteMember Describe(BoundaryMember member, RemoteLayout layout) => new()
    {
        Name = member.Symbol.Name,
        Signature = ContractsTemplate.Signature(member),
        Route = member.Status == "remote" ? "/" + layout.Route(member) : null,
        Status = member.Status,
        Sync = member.Method is not null && !member.Async,
        Problems = member.Problems,
    };

    private static INamedTypeSymbol? Implementation(RemoteRequest request, CSharpCompilation compilation, INamedTypeSymbol contract)
    {
        var location = new DiagnosticLocation(request.Project.Id);
        if (request.Implementation is { } name)
        {
            if (Find(compilation, name) is { TypeKind: TypeKind.Class, IsAbstract: false } named && InSource(compilation, named)
                && named.AllInterfaces.Contains(contract, SymbolEqualityComparer.Default))
            {
                return named;
            }

            request.Diagnostics.Report(DiagnosticCatalog.OFR4023, $"'{name}' is not a class in {request.Project.Id} that implements {contract.Name}.", location);
            return null;
        }

        var candidates = AuditEngine.Sources(compilation)
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Select(c => compilation.GetSemanticModel(t).GetDeclaredSymbol(c)))
            .OfType<INamedTypeSymbol>()
            .Where(t => !t.IsAbstract && t.Interfaces.Contains(contract, SymbolEqualityComparer.Default))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(AuditEngine.Name, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        request.Diagnostics.Report(DiagnosticCatalog.OFR4023,
            candidates.Count == 0
                ? $"No class in {request.Project.Id} implements {contract.Name}; pass --implementation."
                : $"{string.Join(", ", candidates.Select(AuditEngine.Name))} implement {contract.Name}; pass --implementation.",
            location);
        return null;
    }

    private static INamedTypeSymbol? Find(Compilation compilation, string name) =>
        compilation.GetTypeByMetadataName(name)
        ?? compilation.GetSymbolsWithName(n => name.EndsWith(n, StringComparison.Ordinal), SymbolFilter.Type).OfType<INamedTypeSymbol>().FirstOrDefault(t => AuditEngine.Name(t) == name);

    private static bool InSource(Compilation compilation, INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Any(r => compilation.SyntaxTrees.Contains(r.SyntaxTree));

    private static string? Directory(string? directory) => directory is null ? null : RepoPaths.Normalize(directory).TrimEnd('/');

    private static string File(string root, SyntaxTree tree) =>
        Path.IsPathRooted(tree.FilePath) ? RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(tree.FilePath)) : RepoPaths.Normalize(tree.FilePath);

    /// <summary>The nearest Directory.Packages.props above the generated projects turns central package management on.</summary>
    private static bool CentralPackages(string root, string directory)
    {
        for (var current = RepoPaths.ToAbsolute(root, directory); current is not null && current.StartsWith(root, StringComparison.Ordinal); current = Path.GetDirectoryName(current))
        {
            var props = Path.Combine(current, "Directory.Packages.props");
            if (System.IO.File.Exists(props))
            {
                return CentralOn().IsMatch(System.IO.File.ReadAllText(props));
            }
        }

        return false;
    }

    [GeneratedRegex(@"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase)]
    private static partial Regex CentralOn();
}
