using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Progress;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.DeadCode;

public sealed record DeadCodeRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    /// <summary><c>public</c> or <c>all</c>.</summary>
    public string Scope { get; init; } = "all";

    public DeadCodeConfidence MinConfidence { get; init; } = DeadCodeConfidence.Low;

    public bool IncludeTests { get; init; }

    /// <summary>Project ids to report on; empty for every non-test C# project.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary><c>deadCode.externalConsumers</c>: projects or assemblies other repositories consume.</summary>
    public IReadOnlyList<string> ExternalConsumers { get; init; } = [];

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;
}

/// <summary>
/// <c>audit dead-code</c>: types and members nothing in the solution references, with an
/// honest confidence. References are every name the semantic model binds, in every project's
/// recorded compilation, matched by documentation ID (a member's use also counts for the types
/// containing it); uses inside a symbol's own declaration do not count. Implicit uses the
/// compiler makes (<c>foreach</c>, <c>await</c>, collection initializers, deconstruction, query
/// clauses) count too. Overrides, interface implementations, constructors, and operators are
/// never candidates.
/// </summary>
public static class DeadCodeAnalyzer
{
    /// <summary>Calls that register types by convention (Scrutor, Autofac, MediatR, MVC).</summary>
    private static readonly HashSet<string> ConventionCalls = new(StringComparer.Ordinal)
    {
        "Scan", "RegisterAssemblyTypes", "RegisterAssemblyModules", "AddMediatR", "AddControllers", "AddControllersWithViews",
        "AddMvc", "MapControllers", "AddClasses", "FromAssemblyOf", "FromAssemblies", "RegisterTypes",
    };

    /// <summary>Attributes that say nothing about reflection.</summary>
    private static readonly string[] InertAttributePrefixes =
    [
        "System.ObsoleteAttribute", "System.Diagnostics.", "System.ComponentModel.EditorBrowsableAttribute",
        "System.Runtime.CompilerServices.", "System.CLSCompliantAttribute",
    ];

    private static readonly HashSet<string> SerializationAttributes = new(StringComparer.Ordinal)
    {
        "System.SerializableAttribute", "System.Runtime.Serialization.DataContractAttribute", "System.Runtime.Serialization.DataMemberAttribute",
        "System.Xml.Serialization.XmlRootAttribute", "System.Xml.Serialization.XmlTypeAttribute",
    };

    public static DeadCodeResult Analyze(DeadCodeRequest request)
    {
        using var loader = new CompilationLoader(request.RepositoryRoot);
        var skipped = new List<string>();
        var compilations = Load(request, loader, skipped);
        var solutionAssemblies = compilations.Select(c => c.Compilation.AssemblyName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shipped = ShippedProjects.Read(request.RepositoryRoot, request.Model, request.ExternalConsumers);

        Index index;
        using (var phase = request.Progress.BeginPhase("dead code: references", 1, 2))
        {
            index = Index.Build(request.RepositoryRoot, compilations, solutionAssemblies, phase);
        }

        var files = ProjectFiles.Read(request.RepositoryRoot, compilations.Select(c => c.Project), skipped);
        index.AddMarkup(files);
        var mentions = Mentions.Read(request.RepositoryRoot, compilations, files);
        var projects = new List<DeadCodeProject>();
        using (var phase = request.Progress.BeginPhase("dead code: candidates", 2, 2))
        {
            var inScope = compilations.Where(c => !IsTest(c.Project) && (request.Projects.Count == 0 || request.Projects.Contains(c.Project.Id, StringComparer.Ordinal))).ToList();
            for (var i = 0; i < inScope.Count; i++)
            {
                phase.Report(i, inScope.Count, inScope[i].Project.Id);
                if (Project(request, inScope[i], index, mentions, solutionAssemblies, shipped.Of(inScope[i].Project)) is { } project)
                {
                    projects.Add(project);
                }
            }
        }

        Report(request, projects);
        var candidates = projects.SelectMany(p => p.Candidates).ToList();
        return new DeadCodeResult
        {
            Scope = request.Scope,
            MinConfidence = request.MinConfidence,
            IncludeTests = request.IncludeTests,
            Projects = projects,
            Summary = new DeadCodeSummary
            {
                Candidates = candidates.Count,
                High = candidates.Count(c => c.Confidence == DeadCodeConfidence.High),
                Medium = candidates.Count(c => c.Confidence == DeadCodeConfidence.Medium),
                Low = candidates.Count(c => c.Confidence == DeadCodeConfidence.Low),
                TestOnly = projects.Sum(p => p.TestOnly.Count),
                RemovableLoc = candidates.Where(c => c.Confidence == DeadCodeConfidence.High).Sum(c => c.Loc),
            },
            Skipped = [.. skipped.Order(StringComparer.Ordinal)],
        };
    }

    private sealed record Loaded(ProjectInfo Project, Compilation Compilation);

    /// <summary>A test project: <c>IsTestProject</c>, or detected as one (a legacy project referencing NUnit through packages.config).</summary>
    private static bool IsTest(ProjectInfo project) => project.IsTestProject || project.Kind == ProjectKind.Test;

    private static List<Loaded> Load(DeadCodeRequest request, CompilationLoader loader, List<string> skipped)
    {
        var loaded = new List<Loaded>();
        foreach (var project in request.Model.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (project.Config.Excluded)
            {
                continue;
            }

            var compilation = project.Language == "csharp" ? loader.LoadForProject(project) : null;
            var reason = project.Language != "csharp" ? "dead-code analysis reads C# only; its references to C# projects are not seen."
                : compilation is null ? AuditRunner.NoCompilation(project)
                : null;
            if (reason is not null)
            {
                skipped.Add($"{project.Id}: {reason}");
                request.Diagnostics.Report(DiagnosticCatalog.OFR3012, $"Not audited: {reason}", new DiagnosticLocation(project.Id));
                continue;
            }

            loaded.Add(new Loaded(project, compilation!));
        }

        return loaded;
    }

    /// <summary>
    /// The configuration base types an Entity Framework model builder instantiates from an
    /// assembly: EF6 <c>modelBuilder.Configurations.AddFromAssembly</c> creates every
    /// <c>EntityTypeConfiguration&lt;T&gt;</c> and <c>ComplexTypeConfiguration&lt;T&gt;</c>, EF Core
    /// <c>ApplyConfigurationsFromAssembly</c> every <c>IEntityTypeConfiguration&lt;T&gt;</c>.
    /// </summary>
    internal static IReadOnlyList<string> ConfigurationsFromAssembly(IMethodSymbol called) => (called.Name, called.ContainingType?.ToDisplayString()) switch
    {
        ("AddFromAssembly", "System.Data.Entity.ModelConfiguration.Configuration.ConfigurationRegistrar") =>
            ["T:System.Data.Entity.ModelConfiguration.EntityTypeConfiguration`1", "T:System.Data.Entity.ModelConfiguration.ComplexTypeConfiguration`1"],
        ("ApplyConfigurationsFromAssembly", "Microsoft.EntityFrameworkCore.ModelBuilder") => ["T:Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1"],
        _ => [],
    };

    /// <summary>Where the solution uses each symbol (by documentation ID): project, file, and position.</summary>
    private sealed class Index
    {
        private readonly Dictionary<string, List<(string Project, bool Test, string File, int Position)>> _uses = new(StringComparer.Ordinal);

        public List<(string Project, bool Test, string File, int Position)> UsesOf(ISymbol symbol) =>
            symbol.OriginalDefinition.GetDocumentationCommentId() is { } id && _uses.TryGetValue(id, out var uses) ? uses : [];

        private readonly Dictionary<string, string> _discovered = new(StringComparer.Ordinal);

        /// <summary>The convention registration calls found, as "Type.Method at file:line".</summary>
        public List<string> ConventionRegistrations { get; } = [];

        /// <summary>Where the solution first looks for types assignable to <paramref name="type"/> by reflection, or null.</summary>
        public string? DiscoveryOf(INamedTypeSymbol type) =>
            type.OriginalDefinition.GetDocumentationCommentId() is { } id && _discovered.TryGetValue(id, out var where) ? where : null;

        /// <summary>The methods that find types by reflection, by documentation ID, with the type parameters and <c>Type</c> parameters they find types by.</summary>
        private readonly Dictionary<string, HashSet<Slot>> _discoveryMethods = new(StringComparer.Ordinal);

        /// <summary>For a method, the interface members it implements and the methods it overrides: calls through them reach it.</summary>
        private readonly Dictionary<string, List<string>> _aliases = new(StringComparer.Ordinal);

        /// <summary>Calls to solution methods that pass a type (a type argument or a <c>Type</c> argument), in the order met.</summary>
        private readonly List<TypeCall> _calls = [];

        /// <summary>How many times calls are followed back from a discovery method to the methods that call it.</summary>
        private const int DiscoveryDepth = 4;

        /// <summary>A type parameter or a <c>Type</c> parameter of a method, by ordinal.</summary>
        private readonly record struct Slot(bool TypeParameter, int Ordinal);

        /// <summary>What a call passes into one slot: a named type, or a slot of the calling method (<see cref="Caller"/>).</summary>
        private sealed record Flow(Slot Into, INamedTypeSymbol? Type, string? Caller, Slot From);

        private sealed record TypeCall(string Target, string TargetName, IReadOnlyList<Flow> Flows, string Where);

        public static Index Build(string root, List<Loaded> compilations, HashSet<string> solutionAssemblies, IProgressPhase phase)
        {
            var index = new Index();
            var trees = compilations.SelectMany(c => c.Compilation.SyntaxTrees.Select(t => (c.Project, c.Compilation, Tree: t))).ToList();
            var seen = new HashSet<(string, string)>();
            for (var i = 0; i < trees.Count; i++)
            {
                var (project, compilation, tree) = trees[i];
                var file = FileOf(root, tree);
                if (!seen.Add((project.Id, tree.FilePath)))
                {
                    continue;
                }

                phase.Report(i, trees.Count, file);
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    foreach (var symbol in Referenced(model, node))
                    {
                        index.Add(symbol, project, file, node.SpanStart);
                    }

                    if (node is InvocationExpressionSyntax invocation && AuditEngine.Bound(model, invocation) is IMethodSymbol called)
                    {
                        index.AddInvocation(model, invocation, called, $"{file}:{Line(invocation)}", solutionAssemblies);
                    }
                    else if (node is BinaryExpressionSyntax binary && GenericDefinitionCompared(model, binary) is { } generic && Id(generic) is { } genericId)
                    {
                        index._discovered.TryAdd(genericId, $"GetGenericTypeDefinition() == typeof({Display(generic)}) at {file}:{Line(binary)}");
                    }
                }
            }

            index.ResolveDiscoveryCalls();
            index.ConventionRegistrations.Sort(StringComparer.Ordinal);
            return index;
        }

        private void AddInvocation(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol called, string where, HashSet<string> solutionAssemblies)
        {
            if (ConventionCalls.Contains(called.Name))
            {
                ConventionRegistrations.Add($"{called.ContainingType?.Name}.{called.Name} at {where}");
            }

            foreach (var configuration in ConfigurationsFromAssembly(called))
            {
                _discovered.TryAdd(configuration, $"{called.Name} at {where}");
            }

            var discovering = DiscoveredBy(model, invocation, called);
            if (discovering is ITypeOfOperation { TypeOperand: INamedTypeSymbol { TypeKind: not TypeKind.Error } discovered } && Id(discovered) is { } discoveredId)
            {
                _discovered.TryAdd(discoveredId, $"typeof({discovered.Name}).{called.Name} at {where}");
            }
            else if (OwnSlot(discovering) is { } own && Remember(own.Method) is { } method)
            {
                AddDiscovery(method, own.Slot);
            }

            var definition = (called.ReducedFrom ?? called).OriginalDefinition;
            if (definition.ContainingAssembly?.Name is { } assembly && solutionAssemblies.Contains(assembly)
                && (definition.TypeParameters.Length > 0 || definition.Parameters.Any(p => IsSystemType(p.Type))))
            {
                AddCall(model, invocation, where);
            }
        }

        /// <summary>
        /// The operand a reflection check finds types by: <c>X</c> in <c>X.IsAssignableFrom(t)</c>,
        /// <c>t.IsSubclassOf(X)</c>, or <c>t.IsAssignableTo(X)</c>, the way plugin hosts and type
        /// finders discover implementations in the assemblies they load.
        /// </summary>
        private static IOperation? DiscoveredBy(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol called)
        {
            if (!IsSystemType(called.ContainingType) || called.Name is not ("IsAssignableFrom" or "IsSubclassOf" or "IsAssignableTo")
                || model.GetOperation(invocation) is not IInvocationOperation operation)
            {
                return null;
            }

            return Unwrap(called.Name == "IsAssignableFrom" ? operation.Instance : operation.Arguments.FirstOrDefault()?.Value);
        }

        /// <summary>
        /// The slot an operand stands for in the method that owns it: <c>typeof(T)</c> of the
        /// method's type parameter, or one of its <c>Type</c> parameters; null otherwise.
        /// </summary>
        private static (IMethodSymbol Method, Slot Slot)? OwnSlot(IOperation? operand) => operand switch
        {
            ITypeOfOperation { TypeOperand: ITypeParameterSymbol { DeclaringMethod: { MethodKind: MethodKind.Ordinary } method } parameter } => (method, new Slot(true, parameter.Ordinal)),
            IParameterReferenceOperation { Parameter: { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Ordinary } method } parameter } when IsSystemType(parameter.Type)
                => (method, new Slot(false, parameter.Ordinal)),
            _ => null,
        };

        /// <summary>
        /// Records a call to a solution method that passes types: its type arguments, and its
        /// arguments for <c>Type</c> parameters that are <c>typeof(X)</c> or the caller's own type
        /// parameter or <c>Type</c> parameter. Whether the method discovers types by them is
        /// decided once every call is known.
        /// </summary>
        private void AddCall(SemanticModel model, InvocationExpressionSyntax invocation, string where)
        {
            if (model.GetOperation(invocation) is not IInvocationOperation operation || Remember(operation.TargetMethod.OriginalDefinition) is not { } target)
            {
                return;
            }

            var flows = new List<Flow>();
            var method = operation.TargetMethod;
            for (var k = 0; k < method.TypeArguments.Length; k++)
            {
                AddFlow(flows, new Slot(true, k), method.TypeArguments[k] as INamedTypeSymbol, method.TypeArguments[k] as ITypeParameterSymbol);
            }

            foreach (var argument in operation.Arguments.Where(a => a.Parameter is { } p && IsSystemType(p.Type)))
            {
                var into = new Slot(false, argument.Parameter!.Ordinal);
                var value = Unwrap(argument.Value);
                if (value is ITypeOfOperation typeOf)
                {
                    AddFlow(flows, into, typeOf.TypeOperand as INamedTypeSymbol, typeOf.TypeOperand as ITypeParameterSymbol);
                }
                else if (OwnSlot(value) is { } own && Remember(own.Method) is { } caller)
                {
                    flows.Add(new Flow(into, null, caller, own.Slot));
                }
            }

            if (flows.Count > 0)
            {
                _calls.Add(new TypeCall(target, method.Name, flows, where));
            }
        }

        private void AddFlow(List<Flow> flows, Slot into, INamedTypeSymbol? type, ITypeParameterSymbol? parameter)
        {
            if (type is { TypeKind: not TypeKind.Error })
            {
                flows.Add(new Flow(into, type, null, default));
            }
            else if (parameter is { DeclaringMethod: { MethodKind: MethodKind.Ordinary } method } && Remember(method) is { } caller)
            {
                flows.Add(new Flow(into, null, caller, new Slot(true, parameter.Ordinal)));
            }
        }

        /// <summary>
        /// Follows the calls back from the discovery methods, a few levels: a method that passes
        /// its own type parameter or <c>Type</c> parameter into a discovery slot discovers by it
        /// too (<c>FindClassesOfType&lt;T&gt;() =&gt; FindClassesOfType(typeof(T))</c>). Then every
        /// type passed into a discovery slot is found by reflection, where it is passed.
        /// </summary>
        private void ResolveDiscoveryCalls()
        {
            for (var depth = 0; depth < DiscoveryDepth; depth++)
            {
                var changed = false;
                foreach (var call in _calls)
                {
                    if (_discoveryMethods.TryGetValue(call.Target, out var slots))
                    {
                        foreach (var flow in call.Flows.Where(f => f.Caller is not null && slots.Contains(f.Into)).ToList())
                        {
                            changed |= AddDiscovery(flow.Caller!, flow.From);
                        }
                    }
                }

                if (!changed)
                {
                    break;
                }
            }

            foreach (var call in _calls)
            {
                if (!_discoveryMethods.TryGetValue(call.Target, out var slots))
                {
                    continue;
                }

                foreach (var flow in call.Flows.Where(f => f.Type is not null && slots.Contains(f.Into)))
                {
                    if (Id(flow.Type!) is { } id)
                    {
                        var name = Display(flow.Type!);
                        _discovered.TryAdd(id, flow.Into.TypeParameter ? $"{call.TargetName}<{name}> at {call.Where}" : $"{call.TargetName}(typeof({name})) at {call.Where}");
                    }
                }
            }
        }

        /// <summary>Records that a method finds types by a slot, as do the interface members it implements and the methods it overrides.</summary>
        private bool AddDiscovery(string method, Slot slot)
        {
            var changed = false;
            foreach (var id in _aliases.GetValueOrDefault(method, []).Prepend(method))
            {
                var slots = _discoveryMethods.TryGetValue(id, out var existing) ? existing : _discoveryMethods[id] = [];
                changed |= slots.Add(slot);
            }

            return changed;
        }

        /// <summary>A method's documentation ID, remembering the interface members it implements and the methods it overrides.</summary>
        private string? Remember(IMethodSymbol method)
        {
            if (Id(method) is not { } id)
            {
                return null;
            }

            if (!_aliases.ContainsKey(id))
            {
                _aliases[id] = [.. Aliases(method.OriginalDefinition).Select(Id).OfType<string>().Distinct(StringComparer.Ordinal)];
            }

            return id;
        }

        private static IEnumerable<IMethodSymbol> Aliases(IMethodSymbol method)
        {
            foreach (var implemented in method.ExplicitInterfaceImplementations)
            {
                yield return implemented;
            }

            if (method.ContainingType is { } type)
            {
                foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers(method.Name)).OfType<IMethodSymbol>())
                {
                    if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method))
                    {
                        yield return member;
                    }
                }
            }

            for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            {
                yield return overridden;
            }
        }

        /// <summary>
        /// The generic type definition <c>G&lt;&gt;</c> of <c>x.GetGenericTypeDefinition() == typeof(G&lt;&gt;)</c>
        /// (or <c>!=</c>, directly or through a local or a query's <c>let</c>), the way EF6 model
        /// builders find their <c>EntityTypeConfiguration&lt;T&gt;</c> classes; null otherwise.
        /// Definitions from the base class library are left out: comparing with
        /// <c>typeof(Nullable&lt;&gt;)</c> or <c>typeof(IEnumerable&lt;&gt;)</c> inspects a type; it does not find one.
        /// </summary>
        private static INamedTypeSymbol? GenericDefinitionCompared(SemanticModel model, BinaryExpressionSyntax binary)
        {
            if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                return null;
            }

            foreach (var (side, other) in new[] { (binary.Left, binary.Right), (binary.Right, binary.Left) })
            {
                if (Unparenthesized(side) is TypeOfExpressionSyntax typeOf
                    && model.GetTypeInfo(typeOf.Type).Type is INamedTypeSymbol { IsUnboundGenericType: true } generic
                    && !IsBaseClassLibrary(generic.ContainingAssembly)
                    && IsGenericTypeDefinition(model, other, follow: true))
                {
                    return generic;
                }
            }

            return null;
        }

        private static bool IsGenericTypeDefinition(SemanticModel model, ExpressionSyntax expression, bool follow)
        {
            expression = Unparenthesized(expression);
            if (expression is InvocationExpressionSyntax invocation)
            {
                return AuditEngine.Bound(model, invocation) is IMethodSymbol { Name: "GetGenericTypeDefinition" } method && IsSystemType(method.ContainingType);
            }

            if (!follow || expression is not IdentifierNameSyntax name || model.GetSymbolInfo(name).Symbol is not { } variable || variable is not (ILocalSymbol or IRangeVariableSymbol))
            {
                return false;
            }

            foreach (var declaration in variable.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).Where(d => d.SyntaxTree == model.SyntaxTree))
            {
                var value = declaration switch
                {
                    VariableDeclaratorSyntax { Initializer.Value: var initial } => initial,
                    LetClauseSyntax let => let.Expression,
                    _ => null,
                };
                if (value is not null && IsGenericTypeDefinition(model, value, follow: false))
                {
                    return true;
                }
            }

            return false;
        }

        private static ExpressionSyntax Unparenthesized(ExpressionSyntax expression) =>
            expression is ParenthesizedExpressionSyntax parenthesized ? Unparenthesized(parenthesized.Expression) : expression;

        private static IOperation? Unwrap(IOperation? operation)
        {
            while (true)
            {
                switch (operation)
                {
                    case IConversionOperation { IsImplicit: true } conversion:
                        operation = conversion.Operand;
                        break;
                    case IParenthesizedOperation parenthesized:
                        operation = parenthesized.Operand;
                        break;
                    default:
                        return operation;
                }
            }
        }

        private static bool IsBaseClassLibrary(IAssemblySymbol? assembly) =>
            assembly?.Name is "mscorlib" or "netstandard" or "System" || assembly?.Name.StartsWith("System.", StringComparison.Ordinal) == true;

        private static bool IsSystemType(ITypeSymbol? type) => type?.ToDisplayString() == "System.Type";

        private static string? Id(ISymbol symbol) => symbol.OriginalDefinition.GetDocumentationCommentId();

        private static string Display(INamedTypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        /// <summary>
        /// The types ASP.NET markup names for the runtime to create (a page's <c>Inherits</c>, a
        /// handler's <c>Class</c>): uses from the markup file, as real as a use in code.
        /// </summary>
        public void AddMarkup(IEnumerable<ProjectFile> files)
        {
            foreach (var file in files.Where(f => f.Markup))
            {
                foreach (var type in ProjectFiles.NamedTypes(file.Text).Distinct(StringComparer.Ordinal))
                {
                    var id = "T:" + type;
                    var uses = _uses.TryGetValue(id, out var list) ? list : _uses[id] = [];
                    uses.Add((file.Project, false, file.Relative, 0));
                }
            }
        }

        private void Add(ISymbol symbol, ProjectInfo project, string file, int position)
        {
            // A member's use is also a use of the types that contain it (extension methods are
            // called without naming their class).
            for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
            {
                if (current.OriginalDefinition.GetDocumentationCommentId() is not { } id)
                {
                    continue;
                }

                var uses = _uses.TryGetValue(id, out var list) ? list : _uses[id] = [];
                uses.Add((project.Id, IsTest(project), file, position));
            }
        }

        /// <summary>The symbols a node uses, explicitly or through the compiler's pattern lookups.</summary>
        private static IEnumerable<ISymbol> Referenced(SemanticModel model, SyntaxNode node)
        {
            switch (node)
            {
                case SimpleNameSyntax or ElementAccessExpressionSyntax or BaseObjectCreationExpressionSyntax or AttributeSyntax or ConstructorInitializerSyntax:
                    var info = model.GetSymbolInfo(node);
                    foreach (var symbol in info.Symbol is { } bound ? [bound] : info.CandidateSymbols)
                    {
                        yield return Normalize(symbol);
                    }

                    break;
                case CommonForEachStatementSyntax forEach:
                    var loop = model.GetForEachStatementInfo(forEach);
                    foreach (var symbol in new ISymbol?[] { loop.GetEnumeratorMethod, loop.MoveNextMethod, loop.CurrentProperty }.OfType<ISymbol>())
                    {
                        yield return symbol;
                    }

                    break;
                case AwaitExpressionSyntax await:
                    var awaited = model.GetAwaitExpressionInfo(await);
                    foreach (var symbol in new ISymbol?[] { awaited.GetAwaiterMethod, awaited.GetResultMethod, awaited.IsCompletedProperty }.OfType<ISymbol>())
                    {
                        yield return symbol;
                    }

                    break;
                case InitializerExpressionSyntax initializer when initializer.IsKind(SyntaxKind.CollectionInitializerExpression):
                    foreach (var element in initializer.Expressions)
                    {
                        if (model.GetCollectionInitializerSymbolInfo(element).Symbol is { } add)
                        {
                            yield return Normalize(add);
                        }
                    }

                    break;
                case AssignmentExpressionSyntax { Left: TupleExpressionSyntax or DeclarationExpressionSyntax } deconstruction:
                    if (model.GetDeconstructionInfo(deconstruction).Method is { } deconstruct)
                    {
                        yield return Normalize(deconstruct);
                    }

                    break;
                case QueryClauseSyntax or SelectOrGroupClauseSyntax:
                    if (model.GetSymbolInfo(node).Symbol is { } clause)
                    {
                        yield return Normalize(clause);
                    }

                    break;
            }
        }

        /// <summary>Accessors count for their property or event, constructors for their type, reduced extensions for their definition.</summary>
        private static ISymbol Normalize(ISymbol symbol) => symbol switch
        {
            IMethodSymbol { ReducedFrom: { } reduced } => reduced,
            IMethodSymbol { AssociatedSymbol: { } associated } => associated,
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor => constructor.ContainingType,
            _ => symbol,
        };
    }

    /// <summary>
    /// The words of the solution's string literals and of resource, configuration, and markup
    /// files, with where each first appears. A word is a run of identifier characters, so a
    /// name is mentioned when a run equals it: the name as a whole word.
    /// </summary>
    private sealed class Mentions
    {
        private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _anyCase = new(StringComparer.OrdinalIgnoreCase);

        public static Mentions Read(string root, List<Loaded> compilations, List<ProjectFile> files)
        {
            var mentions = new Mentions();
            foreach (var tree in compilations.SelectMany(c => c.Compilation.SyntaxTrees).DistinctBy(t => t.FilePath))
            {
                var file = FileOf(root, tree);
                foreach (var token in tree.GetRoot().DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                    {
                        mentions.Add(token.ValueText, $"{file}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    }
                }
            }

            foreach (var file in files)
            {
                mentions.Add(file.Text, file.Relative);
            }

            return mentions;
        }

        /// <summary>Where the name first appears as a word, or null; <paramref name="ignoreCase"/> for names looked up the way MVC routes, without regard to case.</summary>
        public string? Of(string name, bool ignoreCase) => (ignoreCase ? _anyCase : _exact).GetValueOrDefault(name);

        private void Add(string text, string where)
        {
            for (var at = 0; at < text.Length; at++)
            {
                if (!IsIdentifierChar(text[at]))
                {
                    continue;
                }

                var start = at;
                while (at < text.Length && IsIdentifierChar(text[at]))
                {
                    at++;
                }

                var word = text[start..at];
                _exact.TryAdd(word, where);
                _anyCase.TryAdd(word, where);
            }
        }
    }

    private sealed record Declared(ISymbol Symbol, IReadOnlyList<SyntaxNode> Declarations);

    private static DeadCodeProject? Project(
        DeadCodeRequest request, Loaded loaded, Index index, Mentions mentions, HashSet<string> solutionAssemblies, ShippedReason? shipped)
    {
        var (project, compilation) = loaded;
        var sources = AuditEngine.Sources(compilation).ToHashSet();
        var declared = Declarations(compilation, sources);
        var candidates = new List<DeadCodeCandidate>();
        var testOnly = new List<TestOnlySymbol>();
        var unusedTypes = new List<ISymbol>();

        foreach (var item in declared)
        {
            var symbol = item.Symbol;
            if (unusedTypes.Any(t => Contains(t, symbol)) || !Eligible(symbol) || (request.Scope == "public" && Accessibility(symbol) != "public"))
            {
                continue;
            }

            var outside = index.UsesOf(symbol).Where(u => !InsideDeclarations(item, u.File, u.Position, request.RepositoryRoot)).ToList();
            var (file, line, loc) = Where(request.RepositoryRoot, item);
            if (outside.Count > 0)
            {
                if (request.IncludeTests && outside.All(u => u.Test))
                {
                    testOnly.Add(new TestOnlySymbol
                    {
                        Symbol = AuditEngine.Name(symbol),
                        Kind = Kind(symbol),
                        File = file,
                        Line = line,
                        Loc = loc,
                        Tests = [.. outside.Select(u => u.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    });
                    if (symbol is INamedTypeSymbol)
                    {
                        unusedTypes.Add(symbol);
                    }
                }

                continue;
            }

            if (symbol is INamedTypeSymbol)
            {
                unusedTypes.Add(symbol);
            }

            var (confidence, evidence) = Confidence(project, symbol, index, mentions, solutionAssemblies, shipped);
            if (confidence < request.MinConfidence)
            {
                continue;
            }

            candidates.Add(new DeadCodeCandidate
            {
                Symbol = AuditEngine.Name(symbol),
                Kind = Kind(symbol),
                Accessibility = Accessibility(symbol),
                Confidence = confidence,
                Evidence = evidence,
                File = file,
                Line = line,
                Loc = loc,
            });
        }

        if (candidates.Count == 0 && testOnly.Count == 0)
        {
            return null;
        }

        var ordered = candidates.OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Line).ThenBy(c => c.Symbol, StringComparer.Ordinal).ToList();
        return new DeadCodeProject
        {
            Project = project.Id,
            Candidates = ordered,
            TestOnly = [.. testOnly.OrderBy(t => t.File, StringComparer.Ordinal).ThenBy(t => t.Line).ThenBy(t => t.Symbol, StringComparer.Ordinal)],
            Loc = new DeadCodeLoc(
                ordered.Where(c => c.Confidence == DeadCodeConfidence.High).Sum(c => c.Loc),
                ordered.Where(c => c.Confidence == DeadCodeConfidence.Medium).Sum(c => c.Loc),
                ordered.Where(c => c.Confidence == DeadCodeConfidence.Low).Sum(c => c.Loc)),
        };
    }

    /// <summary>Declared types and members in source order, outer before inner, one entry per symbol (partial declarations together).</summary>
    private static List<Declared> Declarations(Compilation compilation, HashSet<SyntaxTree> sources)
    {
        var bySymbol = new Dictionary<ISymbol, List<SyntaxNode>>(SymbolEqualityComparer.Default);
        var order = new List<ISymbol>();
        foreach (var tree in compilation.SyntaxTrees.Where(sources.Contains))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax))
            {
                var symbols = node switch
                {
                    BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax or BasePropertyDeclarationSyntax or EventDeclarationSyntax
                        => model.GetDeclaredSymbol(node) is { } one ? [one] : [],
                    BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v)).OfType<ISymbol>().ToList(),
                    _ => (IReadOnlyList<ISymbol>)[],
                };
                foreach (var symbol in symbols)
                {
                    if (!bySymbol.TryGetValue(symbol, out var nodes))
                    {
                        bySymbol[symbol] = nodes = [];
                        order.Add(symbol);
                    }

                    nodes.Add(node);
                }
            }
        }

        return [.. order.Select(s => new Declared(s, bySymbol[s]))];
    }

    /// <summary>Whether a symbol can be dead code at all (not an override, implementation, constructor, operator, or interface member).</summary>
    private static bool Eligible(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared || symbol.IsOverride || symbol.IsAbstract || symbol.IsVirtual || symbol.ContainingType?.TypeKind is TypeKind.Interface or TypeKind.Enum)
        {
            return false;
        }

        if (symbol is IMethodSymbol method && (method.MethodKind != MethodKind.Ordinary || method.ExplicitInterfaceImplementations.Length > 0 || method.PartialDefinitionPart is not null))
        {
            return false;
        }

        if (symbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } or IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
        {
            return false;
        }

        return symbol is INamedTypeSymbol || !ImplementsInterface(symbol);
    }

    private static bool ImplementsInterface(ISymbol symbol) =>
        symbol.ContainingType is { } type
        && type.AllInterfaces.SelectMany(i => i.GetMembers()).Any(m => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), symbol));

    private static bool Contains(ISymbol type, ISymbol symbol)
    {
        for (var current = symbol.ContainingType; current is not null; current = current.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InsideDeclarations(Declared declared, string file, int position, string root) =>
        declared.Declarations.Any(d => FileOf(root, d.SyntaxTree) == file && d.FullSpan.Contains(position));

    private static (DeadCodeConfidence Confidence, List<string> Evidence) Confidence(
        ProjectInfo project, ISymbol symbol, Index index, Mentions mentions, HashSet<string> solutionAssemblies, ShippedReason? shipped)
    {
        var evidence = new List<string>();
        DeadCodeConfidence confidence;
        var accessibility = Accessibility(symbol);
        var controller = accessibility == "public" ? ControllerOf(symbol) : null;
        if (controller is not null)
        {
            // MVC finds actions by the request's route, never through code: at most medium.
            confidence = DeadCodeConfidence.Medium;
            if (shipped is not null)
            {
                evidence.Add(Shipped(shipped));
            }

            evidence.Add($"a public action of a controller (derives from {controller}): MVC routes requests to it by name");
        }
        else if (accessibility == "public")
        {
            (confidence, var why) = shipped is null ? (DeadCodeConfidence.High, "public, and the assembly is not packed") : (DeadCodeConfidence.Medium, Shipped(shipped));
            evidence.Add(why);
        }
        else
        {
            var friends = project.InternalsVisibleTo.Where(f => !solutionAssemblies.Contains(f.Split(',')[0].Trim())).Order(StringComparer.Ordinal).ToList();
            (confidence, var why) = accessibility == "internal" && friends.Count > 0
                ? (DeadCodeConfidence.Medium, $"internal, and visible to {string.Join(", ", friends)} outside the solution")
                : (DeadCodeConfidence.High, accessibility);
            evidence.Add(why);
        }

        var low = LowEvidence(symbol, index, mentions, ignoreCase: controller is not null).ToList();
        if (low.Count > 0)
        {
            confidence = DeadCodeConfidence.Low;
            evidence.AddRange(low);
        }

        return (confidence, evidence);
    }

    /// <summary>Why a public symbol of a shipped project is only <c>medium</c> (ADR 0041).</summary>
    private static string Shipped(ShippedReason shipped) => shipped.Rule switch
    {
        ShippedRule.ExternalConsumer => "public, and the assembly is listed in deadCode.externalConsumers",
        ShippedRule.Packable => "public in a packable assembly (IsPackable): other repositories may use it",
        ShippedRule.Nuspec => $"public in an assembly {shipped.Reason}: other repositories may use it",
        _ => "public in a library no application in the solution uses (only tests and other libraries reference it): other repositories may use it",
    };

    /// <summary>What static analysis cannot see: strings, conventions, serializers, entry points, reflection-driven attributes.</summary>
    private static IEnumerable<string> LowEvidence(ISymbol symbol, Index index, Mentions mentions, bool ignoreCase)
    {
        if (mentions.Of(symbol.Name, ignoreCase) is { } where)
        {
            yield return $"the name appears in a string or resource at {where}";
        }

        if (symbol is INamedTypeSymbol type)
        {
            if (Convention(type, index) is { } convention)
            {
                yield return convention;
            }

            if (type.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsStatic && m.Name == "Main") || type.Name == "Program")
            {
                yield return "entry point";
            }
        }

        if (symbol is IMethodSymbol { IsStatic: true, Name: "Main" })
        {
            yield return "entry point";
        }

        if (symbol is IMethodSymbol method && CalledByName(method) is { } byName)
        {
            yield return byName;
        }

        foreach (var attribute in symbol.GetAttributes().Select(a => a.AttributeClass?.ToDisplayString()).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (SerializationAttributes.Contains(attribute))
            {
                yield return $"[{Short(attribute)}]: serializers use it by reflection";
            }
            else if (!InertAttributePrefixes.Any(p => attribute.StartsWith(p, StringComparison.Ordinal)))
            {
                yield return $"[{Short(attribute)}]: frameworks find attributed code by reflection";
            }
        }

        if (symbol is IPropertySymbol or IFieldSymbol && Accessibility(symbol) == "public")
        {
            yield return "public data member: serializers, ORMs, and data binding use them by reflection";
        }
    }

    /// <summary>
    /// Why ASP.NET calls a method by its name, or null: the <c>Page_</c> handlers of pages and
    /// controls (<c>AutoEventWireup</c>), and the <c>Application_</c> and <c>Session_</c>
    /// handlers of <c>Global.asax</c>.
    /// </summary>
    private static string? CalledByName(IMethodSymbol method)
    {
        if (method.Name.StartsWith("Page_", StringComparison.Ordinal) && DerivesFrom(method.ContainingType, "System.Web.UI.TemplateControl"))
        {
            return "ASP.NET calls the Page_ handlers of pages and controls by name (AutoEventWireup)";
        }

        return (method.Name.StartsWith("Application_", StringComparison.Ordinal) || method.Name.StartsWith("Session_", StringComparison.Ordinal))
            && DerivesFrom(method.ContainingType, "System.Web.HttpApplication")
            ? "ASP.NET calls the Application_ and Session_ handlers of Global.asax by name"
            : null;
    }

    private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol? type, string baseType)
    {
        for (var current = type?.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The controller base (<c>Controller</c>, <c>ControllerBase</c>, <c>ApiController</c>) a
    /// public instance method's type derives from, or null: such a method is an action, which
    /// MVC and Web API reach by name from a route.
    /// </summary>
    private static string? ControllerOf(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Microsoft.CodeAnalysis.Accessibility.Public } method
            || method.GetAttributes().Any(a => a.AttributeClass?.Name == "NonActionAttribute"))
        {
            return null;
        }

        return BaseTypes(method.ContainingType).FirstOrDefault(b => b.Name is "Controller" or "ControllerBase" or "ApiController")?.Name;
    }

    /// <summary>Why a type may be created by convention, or null.</summary>
    private static string? Convention(INamedTypeSymbol type, Index index)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
        {
            return null;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name is "Controller" or "ControllerBase" or "ApiController" or "Hub")
            {
                return $"derives from {current.Name}: the framework discovers it";
            }
        }

        if (type.AllInterfaces.Any(i => i.Name is "IRequestHandler" or "INotificationHandler" or "IConsumer" or "IHostedService"))
        {
            return "implements a handler interface that containers discover";
        }

        foreach (var discoverable in BaseTypes(type).Concat(type.AllInterfaces))
        {
            if (index.DiscoveryOf(discoverable) is { } where)
            {
                return $"{(discoverable.TypeKind == TypeKind.Interface ? "implements" : "derives from")} {AuditEngine.Name(discoverable)}, which the solution finds types by with reflection ({where})";
            }
        }

        var solutionInterface = type.AllInterfaces.FirstOrDefault(i => i.Locations.Any(l => l.IsInSource));
        return solutionInterface is not null && index.ConventionRegistrations.Count > 0
            ? $"implements {AuditEngine.Name(solutionInterface)}, and the solution registers types by convention ({index.ConventionRegistrations[0]})"
            : null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Short(string attribute)
    {
        var name = attribute[(attribute.LastIndexOf('.') + 1)..];
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    /// <summary><c>public</c> when visible outside the assembly (public all the way out), else the most restrictive level on the way.</summary>
    private static string Accessibility(ISymbol symbol)
    {
        var result = "public";
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            switch (current.DeclaredAccessibility)
            {
                case Microsoft.CodeAnalysis.Accessibility.Private:
                    return "private";
                case Microsoft.CodeAnalysis.Accessibility.Internal or Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal:
                    result = "internal";
                    break;
            }
        }

        return result;
    }

    private static string Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "delegate",
        INamedTypeSymbol => "class",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "member",
    };

    /// <summary>The first declaration's file and line, and the lines of every declaration with its attributes and documentation comment.</summary>
    private static (string File, int Line, int Loc) Where(string root, Declared declared)
    {
        var first = declared.Declarations[0];
        var loc = 0;
        foreach (var declaration in declared.Declarations)
        {
            var node = declaration;
            var start = Documentation(node);
            var text = node.SyntaxTree.GetText();
            loc += text.Lines.GetLineFromPosition(node.Span.End).LineNumber - text.Lines.GetLineFromPosition(start).LineNumber + 1;
        }

        var line = first.SyntaxTree.GetText().Lines.GetLineFromPosition(first is MemberDeclarationSyntax member ? Identifier(member).SpanStart : first.SpanStart).LineNumber + 1;
        return (FileOf(root, first.SyntaxTree), line, loc);
    }

    private static SyntaxToken Identifier(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier,
        MethodDeclarationSyntax method => method.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        BaseFieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier,
        _ => member.GetFirstToken(),
    };

    /// <summary>
    /// Where a declaration starts counting its documentation comment. Projects that do not
    /// generate documentation parse <c>///</c> as ordinary comments, so both forms count.
    /// </summary>
    private static int Documentation(SyntaxNode node)
    {
        var doc = node.GetLeadingTrivia().FirstOrDefault(t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
            || (t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().StartsWith("///", StringComparison.Ordinal)));
        return doc == default ? node.Span.Start : doc.SpanStart;
    }

    private static string FileOf(string root, SyntaxTree tree) =>
        Path.IsPathRooted(tree.FilePath) ? RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(tree.FilePath)) : tree.FilePath.Replace('\\', '/');

    private static void Report(DeadCodeRequest request, List<DeadCodeProject> projects)
    {
        foreach (var project in projects)
        {
            if (project.Candidates.Count > 0)
            {
                var first = project.Candidates[0];
                var high = project.Candidates.Count(c => c.Confidence == DeadCodeConfidence.High);
                request.Diagnostics.Report(DiagnosticCatalog.OFR3401,
                    $"{project.Candidates.Count} dead-code candidate{(project.Candidates.Count == 1 ? "" : "s")}, {high} at high confidence ({project.Loc.High} lines removable).",
                    new DiagnosticLocation(project.Project, first.File, first.Line),
                    [KeyValuePair.Create<string, JsonNode?>("candidates", project.Candidates.Count), KeyValuePair.Create<string, JsonNode?>("removableLoc", project.Loc.High)]);
            }

            foreach (var symbol in project.TestOnly)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3402,
                    $"{symbol.Symbol} is used only by {string.Join(", ", symbol.Tests)}: move it to the tests or delete it with them.",
                    new DiagnosticLocation(project.Project, symbol.File, symbol.Line));
            }
        }
    }
}
