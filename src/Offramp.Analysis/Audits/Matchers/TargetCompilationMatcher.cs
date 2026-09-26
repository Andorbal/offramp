using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Rules;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>
/// <c>OFR3001</c> and <c>OFR3002</c> from the target compilation. A missing API is an unresolved
/// name on the target (CS0234, CS0246, CS0103, CS1061, CS0117, and CS1069 for a type forwarded to
/// an assembly the target does not reference) whose position resolves to a type
/// or member in the recorded .NET Framework compilation; it carries that assembly's mapping
/// (<c>rules/framework-assemblies.yml</c>). A Windows-only API resolves on the target to a
/// symbol marked <c>[SupportedOSPlatform("windows")]</c> (itself, a containing type, or its
/// assembly); desktop projects, compiled against <c>-windows</c>, have none.
/// </summary>
public sealed class TargetCompilationMatcher : IAuditMatcher
{
    private static readonly HashSet<string> MissingCodes = new(StringComparer.Ordinal) { "CS0234", "CS0246", "CS0103", "CS1061", "CS0117", "CS1069" };

    public string Name => "target-compilation";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Target is not { } target)
        {
            yield break;
        }

        var missing = context.Rule("OFR3001");
        var windowsOnly = target.Windows ? null : context.Rule("OFR3002");
        foreach (var tree in context.Trees)
        {
            if (target.TreeFor(tree) is not { } targetTree)
            {
                continue;
            }

            var targetModel = target.Compilation.GetSemanticModel(targetTree);
            if (missing is not null)
            {
                var recordedModel = context.Compilation.GetSemanticModel(tree);
                foreach (var finding in Missing(missing, tree, recordedModel, targetModel))
                {
                    yield return finding;
                }
            }

            if (windowsOnly is not null)
            {
                foreach (var finding in WindowsOnly(windowsOnly, targetTree, targetModel))
                {
                    yield return finding;
                }
            }
        }
    }

    private static IEnumerable<RawFinding> Missing(AuditRule rule, SyntaxTree tree, SemanticModel recordedModel, SemanticModel targetModel)
    {
        var root = tree.GetRoot();
        foreach (var error in targetModel.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && MissingCodes.Contains(d.Id)).OrderBy(d => d.Location.SourceSpan.Start))
        {
            var node = root.FindNode(error.Location.SourceSpan, getInnermostNodeForTie: true);
            if (TypeOrMember(recordedModel, Rightmost(node)) is not var (name, symbol) || !FromMetadata(symbol))
            {
                continue;
            }

            var assembly = symbol.ContainingAssembly?.Name ?? "";
            var mapping = FrameworkAssemblyMap.Find(assembly);
            var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["assembly"] = assembly,
                ["compilerError"] = error.Id,
                ["mapping"] = Wire(mapping.Kind),
            };
            if (mapping.Package is { } package)
            {
                details["package"] = package;
            }

            if (mapping.Note is { } note)
            {
                details["note"] = note;
            }

            if (mapping.WindowsOnly)
            {
                details["windowsOnly"] = "true";
            }

            var qualified = AuditEngine.Name(symbol);
            var message = $"{qualified} ({assembly}) does not exist on the target. " + Mapping(mapping);
            yield return new RawFinding(rule, name.GetLocation(), qualified, message, details) { Namespace = AuditEngine.NamespaceOf(symbol) };
        }
    }

    private static IEnumerable<RawFinding> WindowsOnly(AuditRule rule, SyntaxTree targetTree, SemanticModel targetModel)
    {
        foreach (var name in targetTree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.Parent is QualifiedNameSyntax qualified && qualified.Left == name)
            {
                continue;
            }

            if (AuditEngine.Bound(targetModel, name) is not { } bound || bound is INamespaceSymbol || !FromMetadata(bound))
            {
                continue;
            }

            var symbol = bound is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType : bound;
            if (WindowsAttribute(bound) is not { } marked)
            {
                continue;
            }

            var qualifiedName = AuditEngine.Name(symbol);
            var details = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["assembly"] = symbol.ContainingAssembly?.Name ?? "", ["platform"] = marked };
            yield return new RawFinding(rule, name.GetLocation(), qualifiedName, $"{qualifiedName} is supported only on {marked} on the target.", details)
            {
                Namespace = AuditEngine.NamespaceOf(symbol),
            };
        }
    }

    /// <summary>
    /// The first type or member at or above a name: in <c>System.Web.UI.Page</c> the target reports
    /// the missing namespace <c>UI</c>, and the API is <c>Page</c>. A name that stays a namespace
    /// (a using directive) is not an API use.
    /// </summary>
    private static (SimpleNameSyntax Name, ISymbol Symbol)? TypeOrMember(SemanticModel model, SimpleNameSyntax? name)
    {
        while (name is not null)
        {
            var symbol = AuditEngine.Bound(model, name);
            if (symbol is not null and not INamespaceSymbol)
            {
                // An attribute (or a constructor call) names its type.
                return (name, symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType : symbol);
            }

            name = name.Parent switch
            {
                QualifiedNameSyntax qualified when qualified.Right == name && qualified.Parent is QualifiedNameSyntax outer && outer.Left == qualified => outer.Right,
                QualifiedNameSyntax qualified when qualified.Left == name => qualified.Right,
                MemberAccessExpressionSyntax access when access.Name == name && access.Parent is MemberAccessExpressionSyntax outer && outer.Expression == access => outer.Name,
                MemberAccessExpressionSyntax access when access.Expression == name => access.Name,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>The platform (<c>windows</c>, <c>windows6.1</c>) when the symbol, a containing type, or its assembly is Windows-only.</summary>
    private static string? WindowsAttribute(ISymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (Supported(current.GetAttributes()) is { } platform)
            {
                return platform;
            }
        }

        return symbol.ContainingAssembly is { } assembly ? Supported(assembly.GetAttributes()) : null;
    }

    private static string? Supported(IEnumerable<AttributeData> attributes)
    {
        var supported = attributes
            .Where(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.SupportedOSPlatformAttribute")
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .OfType<string>()
            .ToList();
        return supported.Count > 0 && supported.All(p => p.StartsWith("windows", StringComparison.OrdinalIgnoreCase)) ? supported.Order(StringComparer.Ordinal).First() : null;
    }

    private static bool FromMetadata(ISymbol symbol) => symbol.Locations.Any(l => l.IsInMetadata);

    private static SimpleNameSyntax? Rightmost(SyntaxNode node) => node switch
    {
        SimpleNameSyntax name => name,
        QualifiedNameSyntax qualified => qualified.Right,
        MemberAccessExpressionSyntax access => access.Name,
        AliasQualifiedNameSyntax alias => alias.Name,
        InvocationExpressionSyntax invocation => Rightmost(invocation.Expression),
        ObjectCreationExpressionSyntax creation => Rightmost(creation.Type),
        AttributeSyntax attribute => Rightmost(attribute.Name),
        ArgumentSyntax argument => Rightmost(argument.Expression),
        _ => node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault(),
    };

    private static string Mapping(FrameworkAssemblyMapping mapping) => mapping.Kind switch
    {
        FrameworkAssemblyKind.Package => $"Package: {mapping.Package}." + (mapping.Note is null ? "" : " " + mapping.Note),
        FrameworkAssemblyKind.CompatPack => $"Package: {mapping.Package} (Windows compatibility pack)." + (mapping.Note is null ? "" : " " + mapping.Note),
        FrameworkAssemblyKind.Builtin => "The assembly is part of the target, but not this API." + (mapping.Note is null ? "" : " " + mapping.Note),
        FrameworkAssemblyKind.None => mapping.Note ?? "No equivalent on the target.",
        _ => "No known mapping for the assembly.",
    };

    private static string Wire(FrameworkAssemblyKind kind) => kind switch
    {
        FrameworkAssemblyKind.Builtin => "builtin",
        FrameworkAssemblyKind.Package => "package",
        FrameworkAssemblyKind.CompatPack => "compat-pack",
        FrameworkAssemblyKind.None => "none",
        _ => "unknown",
    };
}
