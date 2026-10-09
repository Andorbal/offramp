using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Offramp.Analysis.Audits;
using Offramp.Core.Model;
using Graph = Offramp.Analysis.CallGraph.CallGraph;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>
/// The nearest member a test can call to exercise a site (docs/spec/commands/newtonsoft.md,
/// "Entry points"; ADR 0070): breadth first from the site's member up its callers, the first that
/// is accessible, constructible, callable, hermetic, observable, and (for deserialize and DOM
/// sites) takes the JSON as a parameter.
/// </summary>
internal sealed class EntryPointFinder(string repositoryRoot, Graph graph, WorkspaceModel model, int maxDepth, IReadOnlySet<string> nonHermetic)
{
    /// <summary>How far down from a candidate the hermetic check looks.</summary>
    public const int HermeticDepth = 6;

    /// <summary>At most this many examined candidates are listed for an uncovered site.</summary>
    public const int ListedCandidates = 10;

    private readonly Dictionary<string, (UncoveredReason Reason, string Detail)?> _hermetic = new(StringComparer.Ordinal);

    public (SiteEntryPoint? Entry, SiteUncovered? Uncovered) Find(FoundSite site)
    {
        var needsInput = site.Site.Direction != SiteDirection.Serialize;
        var start = (Member: site.Site.Member, Depth: 0, Input: needsInput ? InputOrdinal(JsonArgument(site.Call)) : null, Path: (IReadOnlyList<string>)[site.Site.Member]);
        var queue = new Queue<(string Member, int Depth, int? Input, IReadOnlyList<string> Path)>([start]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { site.Site.Member };
        var examined = new List<EntryCandidate>();
        var exceeded = false;
        while (queue.TryDequeue(out var next))
        {
            var (member, depth, input, path) = next;
            if (graph.Declared(member) is not { } declared)
            {
                continue;
            }

            var failure = Check(declared.Symbol, declared.Project, needsInput, input);
            if (failure is null)
            {
                return (new SiteEntryPoint
                {
                    Member = member,
                    Project = declared.Project.Id,
                    Depth = depth,
                    Path = [.. path.Reverse()],
                    Input = input is { } ordinal ? ((IMethodSymbol)declared.Symbol).Parameters[ordinal].Name : null,
                }, null);
            }

            examined.Add(new EntryCandidate(member, failure.Value.Reason, failure.Value.Detail));
            var callers = graph.CallersOf(member).Where(c => !IsTest(c.Project)).ToList();
            if (depth >= maxDepth)
            {
                exceeded |= callers.Count > 0;
                continue;
            }

            foreach (var call in callers.Where(c => seen.Add(c.Caller)))
            {
                var callerInput = input is { } ordinal ? InputOrdinal(ArgumentAt(call, ordinal)) : null;
                queue.Enqueue((call.Caller, depth + 1, callerInput, [.. path, call.Caller]));
            }
        }

        var nearest = examined.FirstOrDefault();
        return (null, new SiteUncovered
        {
            Reason = exceeded ? UncoveredReason.DepthExceeded : nearest?.Reason ?? UncoveredReason.NotAccessible,
            Detail = exceeded ? $"no caller within {maxDepth} levels qualifies" : nearest?.Detail ?? "the site's member is not declared in source",
            Candidates = [.. examined.Take(ListedCandidates)],
        });
    }

    /// <summary>The first rule a candidate breaks, in the spec's order, or null when it qualifies.</summary>
    private (UncoveredReason Reason, string Detail)? Check(ISymbol member, ProjectInfo project, bool needsInput, int? input)
    {
        if (!Accessible(member))
        {
            return (UncoveredReason.NotAccessible, $"{AuditEngine.Name(member)} is not visible to a test project");
        }

        var type = member.ContainingType;
        if (!member.IsStatic && (type.IsAbstract || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsGenericType
            || type.TypeKind == TypeKind.Class && Generatability.Constructor(type, Accessible) is null))
        {
            return (UncoveredReason.NotConstructible, $"{AuditEngine.Name(type)} has no constructor a probe can call");
        }

        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary, IsGenericMethod: false } method:
                var blocked = method.Parameters.FirstOrDefault(p => p.Ordinal != input && !Generatability.IsOutput(p) && !Generatability.IsGeneratable(p.Type, Accessible));
                if (blocked is not null)
                {
                    return (UncoveredReason.TypeNotGeneratable, $"parameter {blocked.Name} of {AuditEngine.Name(method)} is {AuditEngine.Name(blocked.Type)}");
                }

                break;
            case IPropertySymbol { IsIndexer: false, GetMethod: not null }:
                break;
            default:
                return (UncoveredReason.TypeNotGeneratable, $"{AuditEngine.Name(member)} is not a method or property a probe can call");
        }

        if (Hermetic(CallGraphId(member)) is { } impure)
        {
            return impure;
        }

        if (member is IMethodSymbol { ReturnsVoid: true } voidMethod && !voidMethod.Parameters.Any(Generatability.IsOutput))
        {
            return (UncoveredReason.NotObservable, $"{AuditEngine.Name(member)} returns nothing and has no output parameter");
        }

        if (needsInput && input is null)
        {
            return (UncoveredReason.InputNotParameter, $"the JSON {AuditEngine.Name(member)} reads does not come from one of its parameters");
        }

        return null;
    }

    /// <summary>The first non-hermetic API the member reaches within <see cref="HermeticDepth"/> calls, or null.</summary>
    private (UncoveredReason Reason, string Detail)? Hermetic(string member)
    {
        if (_hermetic.TryGetValue(member, out var known))
        {
            return known;
        }

        var queue = new Queue<(string Member, int Depth)>([(member, 0)]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { member };
        (UncoveredReason, string)? found = null;
        while (found is null && queue.TryDequeue(out var next))
        {
            foreach (var call in graph.CalleesOf(next.Member))
            {
                if (AuditEngine.Keys(call.CalleeSymbol).FirstOrDefault(nonHermetic.Contains) is { } key)
                {
                    var (file, line, _) = AuditEngine.Position(repositoryRoot, call.Node.GetLocation());
                    found = (UncoveredReason.NonHermetic, $"{AuditEngine.Name(call.CalleeSymbol)} ({key}) at {file}:{line}");
                    break;
                }

                if (next.Depth + 1 < HermeticDepth)
                {
                    foreach (var target in graph.ImplementationsOf(call.Callee).Prepend(call.Callee).Where(seen.Add))
                    {
                        queue.Enqueue((target, next.Depth + 1));
                    }
                }
            }
        }

        _hermetic[member] = found;
        return found;
    }

    /// <summary>Public all the way out, or internal with the declaring project's InternalsVisibleTo naming a test project that references it.</summary>
    private bool Accessible(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    continue;
                case Accessibility.Internal or Accessibility.ProtectedOrInternal when FriendTests(current.ContainingAssembly):
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private bool FriendTests(IAssemblySymbol? assembly)
    {
        if (assembly is null || model.Projects.FirstOrDefault(p => string.Equals(p.AssemblyName ?? p.Name, assembly.Name, StringComparison.OrdinalIgnoreCase)) is not { } declaring)
        {
            return false;
        }

        return model.Projects.Any(t => IsTest(t) && t.ProjectReferences.Contains(declaring.Id, StringComparer.Ordinal)
            && declaring.InternalsVisibleTo.Contains(t.AssemblyName ?? t.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsTest(ProjectInfo project) => project.IsTestProject || project.Kind == ProjectKind.Test;

    private static string CallGraphId(ISymbol member) => Graph.Id(member)!;

    /// <summary>The operation that carries the JSON into a deserialize or DOM call: the token <c>ToObject</c> is called on, else its text, reader, or stream argument.</summary>
    private static IOperation? JsonArgument(IInvocationOperation call)
    {
        if (!call.TargetMethod.IsStatic && call.Instance is { } instance && call.TargetMethod.ContainingType.ToDisplayString().StartsWith("Newtonsoft.Json.Linq.", StringComparison.Ordinal))
        {
            return instance;
        }

        return call.Arguments.FirstOrDefault(a => a.Parameter is { } p && (Generatability.IsJsonInput(p.Type) || p.Type.ToDisplayString() == "Newtonsoft.Json.JsonReader"))?.Value;
    }

    /// <summary>The argument a call site passes for a parameter of the member it calls.</summary>
    private static IOperation? ArgumentAt(Offramp.Analysis.CallGraph.CallSite call, int ordinal)
    {
        var invocation = call.Node.AncestorsAndSelf().FirstOrDefault(n => n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax);
        return invocation is null ? null : call.Model.GetOperation(invocation) switch
        {
            IInvocationOperation i => i.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal)?.Value,
            IObjectCreationOperation o => o.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal)?.Value,
            _ => null,
        };
    }

    /// <summary>The parameter (by ordinal) the JSON comes from, following readers, parsed tokens, and locals initialized once; null otherwise.</summary>
    private static int? InputOrdinal(IOperation? operation) => operation switch
    {
        null => null,
        IParameterReferenceOperation parameter => parameter.Parameter.Ordinal,
        IConversionOperation conversion => InputOrdinal(conversion.Operand),
        IParenthesizedOperation parenthesized => InputOrdinal(parenthesized.Operand),
        IObjectCreationOperation creation when creation.Arguments.Length > 0 => InputOrdinal(creation.Arguments[0].Value),
        IPropertyReferenceOperation { Instance: { } token } => InputOrdinal(token),
        IInvocationOperation { TargetMethod: var method } invocation when method.ContainingType.ToDisplayString().StartsWith("Newtonsoft.Json.Linq.", StringComparison.Ordinal)
            || method.ContainingType.ToDisplayString() == "System.Text.Encoding" && method.Name == "GetBytes" =>
            invocation.Instance is { } instance && !method.IsStatic ? InputOrdinal(instance) : invocation.Arguments.Length > 0 ? InputOrdinal(invocation.Arguments[0].Value) : null,
        ILocalReferenceOperation local when local.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
            && local.SemanticModel?.GetOperation(initializer) is { } initial => InputOrdinal(initial),
        _ => null,
    };
}
