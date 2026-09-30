using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Rules;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>
/// <c>OFR3001</c> and <c>OFR3002</c> from the target compilation. A missing API is an unresolved
/// name on the target (CS0234, CS0246, CS0103, CS1061, CS0117, and CS1069 for a type forwarded to
/// an assembly the target does not reference) whose position resolves to a type
/// or member in the recorded .NET Framework compilation that the target does not have; it
/// carries that assembly's mapping (<c>rules/framework-assemblies.yml</c>), and the rule's
/// replacement when it has one. A Windows-only API resolves on the target to a
/// symbol marked <c>[SupportedOSPlatform("windows")]</c> (itself, a containing type, or its
/// assembly); desktop projects, compiled against <c>-windows</c>, have none, but get
/// <c>OFR3003</c> for the Windows Forms types .NET keeps only as throwing shims.
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
        var throws = target.Windows ? context.Rule("OFR3003") : null;
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

            if (throws is not null)
            {
                foreach (var finding in CompatibilityShims(throws, targetTree, targetModel))
                {
                    yield return finding;
                }
            }
        }
    }

    private static IEnumerable<RawFinding> Missing(AuditRule rule, SyntaxTree tree, SemanticModel recordedModel, SemanticModel targetModel)
    {
        var root = tree.GetRoot();
        var targetRoot = targetModel.SyntaxTree.GetRoot();
        foreach (var error in targetModel.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && MissingCodes.Contains(d.Id)).OrderBy(d => d.Location.SourceSpan.Start))
        {
            if (BindsOnTarget(targetModel, Rightmost(targetRoot.FindNode(error.Location.SourceSpan, getInnermostNodeForTie: true))))
            {
                continue;
            }

            var node = root.FindNode(error.Location.SourceSpan, getInnermostNodeForTie: true);
            if (TypeOrMember(recordedModel, Rightmost(node)) is not var (name, symbol) || !FromMetadata(symbol) || ExistsOnTarget(symbol, targetModel.Compilation))
            {
                continue;
            }

            var owner = MissingReceiver(symbol, targetModel.Compilation) ?? symbol;
            var assembly = owner.ContainingAssembly?.Name ?? "";
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

            if (!ReferenceEquals(owner, symbol))
            {
                details["extensionAssembly"] = symbol.ContainingAssembly?.Name ?? "";
            }

            var qualified = AuditEngine.Name(symbol);
            var message = $"{qualified} ({assembly}) does not exist on the target. " + Mapping(mapping);
            if (Replacement(rule, symbol) is { } replacement)
            {
                details["replacement"] = replacement;
                message = $"{qualified} ({assembly}) does not exist on the target. Use {replacement}.";
            }

            yield return new RawFinding(rule, name.GetLocation(), qualified, message, details) { Namespace = AuditEngine.NamespaceOf(owner) };
        }
    }

    /// <summary>
    /// For an extension method called on a type the target does not have (<c>request.IsHttps()</c>
    /// over <c>System.Web.HttpRequestBase</c>), that type: the method is missing because its
    /// receiver is, so the finding belongs to the receiver's assembly, not the solution's one
    /// that declares the method. Null otherwise.
    /// </summary>
    private static INamedTypeSymbol? MissingReceiver(ISymbol symbol, Compilation target)
    {
        if (symbol is not IMethodSymbol { IsExtensionMethod: true } method
            || (method.ReducedFrom is not null ? method.ReceiverType : method.Parameters.FirstOrDefault()?.Type) is not INamedTypeSymbol receiver)
        {
            return null;
        }

        var definition = receiver.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, method.ContainingAssembly))
        {
            return null;
        }

        return target.GetTypesByMetadataName(MetadataName(definition)).IsEmpty ? definition : null;
    }

    /// <summary>The name <see cref="Compilation.GetTypeByMetadataName"/> takes: namespace, nested types joined by <c>+</c>, arity suffixes.</summary>
    private static string MetadataName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            name = outer.MetadataName + "+" + name;
        }

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    /// <summary>The rule's replacement for the API, matched like its symbols (an <c>M:</c> without parameters covers every overload).</summary>
    private static string? Replacement(AuditRule rule, ISymbol symbol) =>
        rule.Replacements.Count == 0 ? null : AuditEngine.Keys(symbol).Select(k => rule.Replacements.GetValueOrDefault(k)).FirstOrDefault(r => r is not null);

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

    /// <summary>
    /// Whether the name the target reports an error at still exists there. A type missing on the
    /// target is reported wherever it is reached, not only where it is named: at every name
    /// looked up inside a class whose base chain contains it (in a <c>UserControl</c> subclass,
    /// <c>Convert.ToInt32</c> carries "<c>UserControl</c> could not be found" although
    /// <c>Convert</c> resolves), and at a call to a method whose signature contains it (the
    /// method is a candidate that failed overload resolution). Such an error is about that type,
    /// which is reported where the code names or inherits it, not about the name.
    /// </summary>
    private static bool BindsOnTarget(SemanticModel targetModel, SimpleNameSyntax? name)
    {
        if (name is null)
        {
            return false;
        }

        var info = targetModel.GetSymbolInfo(name);
        return info.Symbol is { Kind: not SymbolKind.ErrorType } || !info.CandidateSymbols.IsEmpty;
    }

    /// <summary>
    /// Whether the API the recorded compilation names exists on the target all the same: the
    /// error is then about something else, typically a missing base type through which the name
    /// was looked up (<c>Component.DesignMode</c> inside a class deriving from a missing
    /// <c>Control</c>). An API whose signature has a type the target lacks does not exist.
    /// </summary>
    private static bool ExistsOnTarget(ISymbol symbol, Compilation target)
    {
        var definition = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol.OriginalDefinition;
        return definition.GetDocumentationCommentId() is { } id
            && DocumentationCommentId.GetFirstSymbolForDeclarationId(id, target) is { } found
            && !Signature(found).Any(t => t.TypeKind == TypeKind.Error);
    }

    private static IEnumerable<ITypeSymbol> Signature(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters.Select(p => p.Type).Append(method.ReturnType),
        IPropertySymbol property => property.Parameters.Select(p => p.Type).Append(property.Type),
        IFieldSymbol field => [field.Type],
        IEventSymbol @event => [@event.Type],
        _ => [],
    };

    /// <summary>
    /// <c>OFR3003</c> on a <c>-windows</c> target: the Windows Forms types .NET keeps only for
    /// binary compatibility (<c>MenuItem</c>, <c>ContextMenu</c>, <c>MainMenu</c>, <c>DataGrid</c>,
    /// <c>StatusBar</c>, <c>ToolBar</c>). They compile, marked <c>[Obsolete]</c> with
    /// <c>WFDEV006</c>, and throw at run time.
    /// </summary>
    private static IEnumerable<RawFinding> CompatibilityShims(AuditRule rule, SyntaxTree targetTree, SemanticModel targetModel)
    {
        foreach (var name in targetTree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if ((name.Parent is QualifiedNameSyntax qualified && qualified.Left == name)
                || AuditEngine.Bound(targetModel, name) is not { } bound || bound is INamespaceSymbol || !FromMetadata(bound))
            {
                continue;
            }

            var symbol = bound is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType : bound;
            if (Shim(symbol) is not { } shim)
            {
                continue;
            }

            var shimName = AuditEngine.Name(shim);
            var details = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["assembly"] = shim.ContainingAssembly?.Name ?? "", ["diagnosticId"] = ThrowingShim };
            yield return new RawFinding(rule, name.GetLocation(), shimName,
                $"{shimName} compiles on the target but throws at run time: .NET keeps it only for binary compatibility ({ThrowingShim}). Use MenuStrip, ContextMenuStrip, and ToolStripMenuItem for menus, DataGridView for DataGrid, StatusStrip for StatusBar, and ToolStrip for ToolBar.",
                details)
            {
                Namespace = AuditEngine.NamespaceOf(shim),
            };
        }
    }

    /// <summary>The obsoletion .NET gives the Windows Forms types it keeps only so old binaries load.</summary>
    private const string ThrowingShim = "WFDEV006";

    /// <summary>The symbol, or its innermost containing type, marked as a <see cref="ThrowingShim"/>; null when none is.</summary>
    private static ISymbol? Shim(ISymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute"
                && a.NamedArguments.Any(n => n.Key == "DiagnosticId" && n.Value.Value is ThrowingShim)))
            {
                return current is INamedTypeSymbol type ? type.OriginalDefinition : current.OriginalDefinition;
            }
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
