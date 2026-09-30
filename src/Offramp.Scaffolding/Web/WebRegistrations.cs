using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Offramp.Analysis.Audits;

namespace Offramp.Scaffolding.Web;

/// <summary>What <see cref="WebRegistrations"/> found: routes, attribute routing, filters, bundles, in source order.</summary>
internal sealed record WebRegistrationsResult(
    List<WebRoute> Routes,
    List<string> AttributeRouting,
    List<string> GlobalFilters,
    List<WebFilterRegistration> ContainerFilters,
    List<string> Bundles);

/// <summary>
/// The route, filter, and bundle registrations of an application and of the libraries it
/// references (docs/decisions/0059-follow-route-helpers-and-read-the-application-closure.md),
/// read with the semantic model. A registration is a call of <c>MapRoute</c>,
/// <c>MapHttpRoute</c>, <c>IgnoreRoute</c>, <c>RouteCollection.Add</c>,
/// <c>MapODataServiceRoute</c>, <c>GlobalFilters.Filters.Add</c>, Autofac's
/// <c>As*FilterFor&lt;TController&gt;</c>, or <c>BundleCollection.Add</c>. When the name,
/// template, defaults, or area of a route come from the parameters of the method (or local
/// function) that registers it, that method is a helper of the codebase's own
/// (<c>MapLocalizedRoute</c>): its calls are the routes, with the helper's parameters
/// replaced by each call's arguments, followed up to <see cref="MaxDepth"/> calls away.
/// Values that are not literals are kept as the code that computes them.
/// </summary>
internal sealed class WebRegistrations
{
    /// <summary>How many helper calls away from a registration its values are looked for.</summary>
    public const int MaxDepth = 5;

    /// <summary>How many locals, properties, and fields a value is followed through.</summary>
    private const int MaxValueDepth = 4;

    private static readonly string[] FilterKinds = ["Action", "Result", "Exception", "Authorization", "Authentication"];

    private readonly string _root;
    private readonly List<Compilation> _compilations;
    private readonly Dictionary<SyntaxTree, Compilation> _compilationOf = [];
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];
    private readonly Dictionary<string, List<InvocationExpressionSyntax>> _invocations = new(StringComparer.Ordinal);
    private readonly Dictionary<IMethodSymbol, List<IInvocationOperation>> _callSites = new(SymbolEqualityComparer.Default);

    private WebRegistrations(string root, List<Compilation> compilations)
    {
        _root = root;
        _compilations = compilations;
        foreach (var compilation in compilations)
        {
            foreach (var tree in AuditEngine.Sources(compilation).Where(t => WebInventory.FileOf(root, t) is not null).OrderBy(t => t.FilePath, StringComparer.Ordinal))
            {
                _compilationOf.TryAdd(tree, compilation);
            }
        }

        foreach (var tree in _compilationOf.Keys.OrderBy(t => t.FilePath, StringComparer.Ordinal))
        {
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (Name(invocation) is { } name)
                {
                    (_invocations.TryGetValue(name, out var list) ? list : _invocations[name] = []).Add(invocation);
                }
            }
        }
    }

    /// <summary>The registrations in the application's compilation and its libraries' (the projects it references).</summary>
    public static WebRegistrationsResult Find(string root, Compilation application, IReadOnlyList<Compilation> libraries)
    {
        var finder = new WebRegistrations(root, [application, .. libraries.Where(l => l.Language == LanguageNames.CSharp)]);
        return finder.Find();
    }

    private WebRegistrationsResult Find()
    {
        var routes = new List<(Site Site, WebRoute Route)>();
        var attributeRouting = new List<(Site Site, string Kind)>();
        var globalFilters = new List<(Site Site, string Filter)>();
        var containerFilters = new List<WebFilterRegistration>();
        var bundles = new List<(Site Site, string Path)>();
        foreach (var name in _invocations.Keys.Where(Candidate).Order(StringComparer.Ordinal))
        {
            foreach (var syntax in _invocations[name])
            {
                if (Operation(syntax) is not { } invocation)
                {
                    continue;
                }

                var method = Unreduced(invocation.TargetMethod);
                var container = method.ContainingType?.ToDisplayString() ?? "";
                var site = SiteOf(syntax);
                if (RouteShape(invocation, method, container) is { } shape)
                {
                    foreach (var resolved in Resolve(shape, 0))
                    {
                        routes.Add((resolved.Site, Route(resolved)));
                    }
                }
                else if (method.Name == "MapMvcAttributeRoutes" || method.Name == "MapHttpAttributeRoutes")
                {
                    attributeRouting.Add((site, method.Name == "MapMvcAttributeRoutes" ? "mvc" : "webapi"));
                }
                else if (method.Name == "Add" && container is "System.Web.Mvc.GlobalFilterCollection" or "System.Web.Http.Filters.HttpFilterCollection")
                {
                    if (Strip(invocation.Arguments.FirstOrDefault()?.Value) is IObjectCreationOperation { Type: { } filter })
                    {
                        globalFilters.Add((site, Strip(filter.Name)));
                    }
                }
                else if (method.Name == "Add" && container == "System.Web.Optimization.BundleCollection")
                {
                    if (BundlePath(invocation.Arguments.FirstOrDefault()?.Value) is { } path)
                    {
                        bundles.Add((site, path));
                    }
                }
                else if (AutofacFilter(method) is { } kind)
                {
                    containerFilters.Add(ContainerFilter(invocation, method, kind, site));
                }
            }
        }

        return new WebRegistrationsResult(
            [.. routes.OrderBy(r => r.Site).ThenBy(r => r.Route.Name, StringComparer.Ordinal).ThenBy(r => r.Route.Template, StringComparer.Ordinal).Select(r => r.Route)],
            [.. attributeRouting.OrderBy(a => a.Site).Select(a => a.Kind).Distinct(StringComparer.Ordinal)],
            [.. globalFilters.OrderBy(f => f.Site).Select(f => f.Filter)],
            [.. containerFilters.OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Kind, StringComparer.Ordinal)],
            [.. bundles.OrderBy(b => b.Site).Select(b => b.Path)]);
    }

    /// <summary>Names of calls that can be registrations (the semantic model decides), before anything is bound.</summary>
    private static bool Candidate(string name) =>
        name is "MapRoute" or "MapHttpRoute" or "IgnoreRoute" or "Add" or "MapODataServiceRoute" or "MapODataRoute" or "MapMvcAttributeRoutes" or "MapHttpAttributeRoutes"
        || (name.StartsWith("As", StringComparison.Ordinal) && name.Contains("FilterFor", StringComparison.Ordinal));

    // ---- Routes ----

    /// <summary>A route as far as one call says: values may still be parameters of the method the call is in.</summary>
    private sealed record Shape(string Kind, Value Name, Value Template, Defaults Defaults, Value Area, string? Helper, Site Site);

    /// <summary>The route a registration call declares, or null when the call is not one.</summary>
    private Shape? RouteShape(IInvocationOperation invocation, IMethodSymbol method, string container)
    {
        var site = SiteOf(invocation.Syntax);
        switch (method.Name)
        {
            case "MapRoute" when container is "System.Web.Mvc.RouteCollectionExtensions" or "System.Web.Mvc.AreaRegistrationContext":
            {
                var defaults = EvalDefaults(Argument(invocation, "defaults"));
                var area = container == "System.Web.Mvc.AreaRegistrationContext"
                    ? Value.Literal(WebInventory.AreaName(_compilationOf[invocation.Syntax.SyntaxTree], EnclosingType(invocation)))
                    : Value.Null;
                return new Shape("mvc", Eval(Argument(invocation, "name")), Eval(Argument(invocation, "url")), defaults, DataTokensArea(invocation.Syntax) ?? area, null, site);
            }

            case "MapHttpRoute" when container == "System.Web.Http.HttpRouteCollectionExtensions":
                return new Shape("webapi", Eval(Argument(invocation, "name")), Eval(Argument(invocation, "routeTemplate")), EvalDefaults(Argument(invocation, "defaults")), Value.Null, null, site);
            case "IgnoreRoute" when container == "System.Web.Mvc.RouteCollectionExtensions":
                return new Shape("ignore", Value.Null, Eval(Argument(invocation, "url")), Defaults.None, Value.Null, null, site);
            case "Add" when Is(invocation.Instance?.Type, "System.Web.Routing.RouteCollection"):
            {
                var item = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Type.Name == "RouteBase")?.Value;
                if (item is null || OwnCollection(invocation.Instance))
                {
                    return null;
                }

                var (template, defaults, area) = EvalRoute(item);
                return new Shape("mvc", Eval(Argument(invocation, "name")), template, defaults, area, null, site);
            }

            case "MapODataServiceRoute" or "MapODataRoute" when method.ContainingNamespace?.ToDisplayString().Contains("OData", StringComparison.Ordinal) == true:
                return new Shape("odata", Eval(Argument(invocation, "routeName")), Eval(Argument(invocation, "routePrefix")), Defaults.None, Value.Null, null, site);
            default:
                return null;
        }
    }

    /// <summary>A RouteCollection the method creates itself (a filtered copy of the route table), which is not the application's.</summary>
    private bool OwnCollection(IOperation? receiver) =>
        Strip(receiver) is ILocalReferenceOperation { Local: var local } && Strip(Initializer(local)) is IObjectCreationOperation;

    /// <summary>
    /// The routes a shape stands for: itself when its values are known or computed; else the
    /// calls of the helper whose parameters it depends on, with the arguments put in.
    /// </summary>
    private IEnumerable<Shape> Resolve(Shape shape, int depth)
    {
        var owners = Parameters(shape).Select(p => (IMethodSymbol)p.ContainingSymbol).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
        if (owners.Count == 0)
        {
            yield return shape;
            yield break;
        }

        if (owners.Count > 1 || depth >= MaxDepth)
        {
            // Not followed further: what the parameters hold is not known here.
            yield return shape with { Name = shape.Name.Opaque(), Template = shape.Template.Opaque(), Defaults = shape.Defaults.Opaque(), Area = shape.Area.Opaque() };
            yield break;
        }

        var helper = owners[0];
        foreach (var call in CallSites(helper))
        {
            var substituted = new Shape(
                shape.Kind,
                Substitute(shape.Name, helper, call),
                Substitute(shape.Template, helper, call),
                Substitute(shape.Defaults, helper, call),
                DataTokensArea(call.Syntax) ?? Substitute(shape.Area, helper, call),
                HelperName(helper),
                SiteOf(call.Syntax));
            foreach (var resolved in Resolve(substituted, depth + 1))
            {
                yield return resolved;
            }
        }
    }

    private static IEnumerable<IParameterSymbol> Parameters(Shape shape) =>
        shape.Name.Parameters.Concat(shape.Template.Parameters).Concat(shape.Area.Parameters).Concat(shape.Defaults.Parameters);

    /// <summary>The calls of a helper in the application and its libraries, in source order.</summary>
    private List<IInvocationOperation> CallSites(IMethodSymbol helper)
    {
        if (_callSites.TryGetValue(helper, out var cached))
        {
            return cached;
        }

        var result = new List<IInvocationOperation>();
        foreach (var syntax in _invocations.TryGetValue(helper.Name, out var candidates) ? candidates : [])
        {
            if (Operation(syntax) is { } call && SameMethod(Unreduced(call.TargetMethod).OriginalDefinition, helper.OriginalDefinition))
            {
                result.Add(call);
            }
        }

        _callSites[helper] = result;
        return result;
    }

    /// <summary>The same method, also when one side is the other's metadata symbol in a referencing project's compilation.</summary>
    private static bool SameMethod(IMethodSymbol target, IMethodSymbol helper)
    {
        if (SymbolEqualityComparer.Default.Equals(target, helper))
        {
            return true;
        }

        return target.MethodKind != MethodKind.LocalFunction && helper.MethodKind != MethodKind.LocalFunction
            && string.Equals(target.ContainingAssembly?.Name, helper.ContainingAssembly?.Name, StringComparison.Ordinal)
            && DocumentationCommentId.CreateDeclarationId(target) is { } id && id == DocumentationCommentId.CreateDeclarationId(helper);
    }

    private static string HelperName(IMethodSymbol helper) =>
        helper.MethodKind == MethodKind.LocalFunction ? helper.Name : $"{helper.ContainingType?.Name}.{helper.Name}";

    private static WebRoute Route(Shape shape)
    {
        var name = shape.Name.AsLiteral(out var nameText) ? nameText ?? (shape.Kind == "ignore" ? "(ignored)" : "") : "(computed)";
        var computed = !shape.Template.AsLiteral(out var template);
        var area = shape.Area.AsLiteral(out var areaText) ? areaText : null;
        area ??= shape.Defaults.Entries.FirstOrDefault(e => string.Equals(e.Name, "area", StringComparison.OrdinalIgnoreCase)).Value is { } value && value.AsLiteral(out var fromDefaults)
            ? fromDefaults
            : null;
        return new WebRoute
        {
            Name = name,
            Template = computed ? "(computed)" : template ?? "",
            Computed = computed ? shape.Template.Display() : null,
            Kind = shape.Kind,
            Area = string.IsNullOrEmpty(area) ? null : area,
            Defaults = [.. shape.Defaults.Entries.Select(e => e.Name + " = " + e.Value.DefaultText())],
            Helper = shape.Helper,
            File = shape.Site.File,
            Line = shape.Site.Line,
        };
    }

    /// <summary>The template, defaults, and area of a route object passed to <c>RouteCollection.Add</c>.</summary>
    private (Value Template, Defaults Defaults, Value Area) EvalRoute(IOperation route, int depth = 0)
    {
        var operation = Strip(route);
        if (operation is ILocalReferenceOperation local && depth < MaxValueDepth && Initializer(local.Local) is { } initializer)
        {
            var (template, defaults, area) = EvalRoute(initializer, depth + 1);
            return (template, defaults, LocalDataTokensArea(local.Local) ?? area);
        }

        if (operation is not IObjectCreationOperation creation || !Is(creation.Type, "System.Web.Routing.Route"))
        {
            return (Value.Code(operation?.Syntax.ToString() ?? ""), Defaults.None, Value.Null);
        }

        var url = Argument(creation.Arguments, "url") ?? creation.Arguments.FirstOrDefault(a => a.Parameter?.Type.SpecialType == SpecialType.System_String)?.Value;
        var result = (Template: Eval(url), Defaults: EvalDefaults(Argument(creation.Arguments, "defaults")), Area: Area(EvalDefaults(Argument(creation.Arguments, "dataTokens"))));
        foreach (var assignment in creation.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>() ?? [])
        {
            switch ((assignment.Target as IPropertyReferenceOperation)?.Property.Name)
            {
                case "Url":
                    result.Template = Eval(assignment.Value);
                    break;
                case "Defaults":
                    result.Defaults = EvalDefaults(assignment.Value);
                    break;
                case "DataTokens":
                    result.Area = Area(EvalDefaults(assignment.Value));
                    break;
            }
        }

        return result;
    }

    private static Value Area(Defaults tokens) =>
        tokens.Entries.FirstOrDefault(e => string.Equals(e.Name, "area", StringComparison.OrdinalIgnoreCase)).Value ?? Value.Null;

    /// <summary>
    /// <c>.DataTokens["area"] = X</c> (or <c>.DataTokens.Add("area", X)</c>) on the route a call
    /// returns, chained or through the local it is assigned to; null when there is none.
    /// </summary>
    private Value? DataTokensArea(SyntaxNode call)
    {
        var node = call;
        while (node.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            node = node.Parent;
        }

        if (node.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "DataTokens" } access && access.Expression == node && AreaAssigned(access) is { } chained)
        {
            return chained;
        }

        return node.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } && Model(declarator.SyntaxTree).GetDeclaredSymbol(declarator) is ILocalSymbol local
            ? LocalDataTokensArea(local)
            : null;
    }

    private Value? LocalDataTokensArea(ILocalSymbol local)
    {
        if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not { } declarator || Body(declarator) is not { } body)
        {
            return null;
        }

        var model = Model(declarator.SyntaxTree);
        foreach (var access in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Name.Identifier.ValueText == "DataTokens" && access.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == local.Name
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, local) && AreaAssigned(access) is { } area)
            {
                return area;
            }
        }

        return null;
    }

    /// <summary>The value <c>DataTokens["area"]</c> is set to by the expression around <paramref name="dataTokens"/>, or null.</summary>
    private Value? AreaAssigned(MemberAccessExpressionSyntax dataTokens)
    {
        var model = Model(dataTokens.SyntaxTree);
        if (dataTokens.Parent is ElementAccessExpressionSyntax { ArgumentList.Arguments: [var key] } element && element.Parent is AssignmentExpressionSyntax assignment
            && assignment.Left == element && IsArea(model, key.Expression))
        {
            return Eval(model.GetOperation(assignment.Right));
        }

        if (dataTokens.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" } add && add.Parent is InvocationExpressionSyntax { ArgumentList.Arguments: [var name, var value] }
            && IsArea(model, name.Expression))
        {
            return Eval(model.GetOperation(value.Expression));
        }

        return null;
    }

    private static bool IsArea(SemanticModel model, ExpressionSyntax key) =>
        model.GetConstantValue(key).Value is string text && string.Equals(text, "area", StringComparison.OrdinalIgnoreCase);

    // ---- Filters and bundles ----

    /// <summary>The kind of filter an Autofac registration method registers (<c>AsActionFilterFor</c>: action), or null.</summary>
    private static string? AutofacFilter(IMethodSymbol method)
    {
        if (method.ContainingNamespace?.ToDisplayString() is not ("Autofac.Integration.Mvc" or "Autofac.Integration.WebApi") || !method.Name.StartsWith("As", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = method.Name[2..];
        rest = rest.StartsWith("WebApi", StringComparison.Ordinal) ? rest[6..] : rest;
        foreach (var kind in FilterKinds)
        {
            if (!rest.StartsWith(kind + "Filter", StringComparison.Ordinal))
            {
                continue;
            }

            var tail = rest[(kind.Length + "Filter".Length)..];
            var overriding = tail.StartsWith("Override", StringComparison.Ordinal);
            tail = overriding ? tail["Override".Length..] : tail;
            if (tail is "For" or "ForAllControllers")
            {
                return kind.ToLowerInvariant() + (overriding ? " override" : "");
            }
        }

        return null;
    }

    private static WebFilterRegistration ContainerFilter(IInvocationOperation invocation, IMethodSymbol method, string kind, Site site)
    {
        var receiver = invocation.Instance ?? invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value;
        var controller = invocation.TargetMethod.TypeArguments.FirstOrDefault();
        var selector = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "actionSelector")?.Value;
        return new WebFilterRegistration
        {
            Filter = FilterType(receiver)?.ToDisplayString() ?? receiver?.Syntax.ToString() ?? "",
            Kind = kind,
            Controller = method.Name.EndsWith("AllControllers", StringComparison.Ordinal) ? null : controller?.ToDisplayString(),
            Action = selector?.DescendantsAndSelf().OfType<IInvocationOperation>().FirstOrDefault()?.TargetMethod.Name,
            File = site.File,
            Line = site.Line,
        };
    }

    /// <summary>The registered class: <c>TLimit</c> of the registration builder, down the fluent chain (<c>RegisterType&lt;T&gt;()</c>).</summary>
    private static ITypeSymbol? FilterType(IOperation? receiver)
    {
        for (var current = receiver; current is not null;)
        {
            if (current is IConversionOperation conversion)
            {
                current = conversion.Operand;
                continue;
            }

            if (current.Type is INamedTypeSymbol { Name: "IRegistrationBuilder", TypeArguments: [var limit, ..] } && limit.SpecialType != SpecialType.System_Object)
            {
                return limit;
            }

            current = current is IInvocationOperation call ? call.Instance ?? call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value : null;
        }

        return null;
    }

    /// <summary>The virtual path of the bundle a <c>BundleCollection.Add</c> adds, also through a local.</summary>
    private string? BundlePath(IOperation? argument, int depth = 0)
    {
        var operation = Strip(argument);
        if (operation is ILocalReferenceOperation local && depth < MaxValueDepth && Initializer(local.Local) is { } initializer)
        {
            return BundlePath(initializer, depth + 1);
        }

        return operation?.DescendantsAndSelf().OfType<IObjectCreationOperation>().FirstOrDefault()?.Arguments.FirstOrDefault()?.Value.ConstantValue.Value as string;
    }

    // ---- Values ----

    /// <summary>A string value as the code has it: literal parts, parameters of the method it is in, and code computed at run time.</summary>
    private Value Eval(IOperation? operation, int depth = 0)
    {
        var current = Strip(operation);
        if (current is null)
        {
            return Value.Null;
        }

        if (current.ConstantValue.HasValue)
        {
            return Value.Literal(current.ConstantValue.Value is { } constant ? Convert.ToString(constant, CultureInfo.InvariantCulture) : null);
        }

        switch (current)
        {
            case IParameterReferenceOperation { Parameter: var parameter } when parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction }:
                return Value.Of(new ParameterPart(parameter));
            case IFieldReferenceOperation { Field.Name: "Optional", Field.ContainingType.Name: "UrlParameter" or "RouteParameter" }:
                return Value.Of(OptionalPart.Instance);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary when binary.Type?.SpecialType == SpecialType.System_String:
                return Value.Concat(Eval(binary.LeftOperand, depth), Eval(binary.RightOperand, depth));
            case IInterpolatedStringOperation interpolated:
                return Value.Concat([.. interpolated.Parts.Select(part => part switch
                {
                    IInterpolatedStringTextOperation text => Eval(text.Text, depth),
                    IInterpolationOperation { Alignment: null, FormatString: null } hole => Eval(hole.Expression, depth),
                    _ => Value.Code(part.Syntax.ToString()),
                })]);
            case ILocalReferenceOperation local when depth < MaxValueDepth && Initializer(local.Local) is { } initializer:
                return Eval(initializer, depth + 1);
            case IPropertyReferenceOperation { Property: { IsStatic: true, SetMethod: null, Parameters.Length: 0 } property } when depth < MaxValueDepth && Getter(property) is { } getter:
                return Eval(getter, depth + 1) is { IsLiteral: true } returned ? returned : Value.Code(current.Syntax.ToString());
            case IFieldReferenceOperation { Field: { IsStatic: true, IsReadOnly: true } field } when depth < MaxValueDepth && FieldInitializer(field) is { } fieldInitializer:
                return Eval(fieldInitializer, depth + 1) is { IsLiteral: true } initial ? initial : Value.Code(current.Syntax.ToString());
            default:
                return Value.Code(current.Syntax.ToString());
        }
    }

    /// <summary>Route defaults (or data tokens): an anonymous object or a RouteValueDictionary, a parameter, or nothing known.</summary>
    private Defaults EvalDefaults(IOperation? operation, int depth = 0)
    {
        var current = Strip(operation);
        switch (current)
        {
            case IAnonymousObjectCreationOperation anonymous:
                return new Defaults([.. anonymous.Initializers.OfType<ISimpleAssignmentOperation>()
                    .Where(i => i.Target is IPropertyReferenceOperation)
                    .Select(i => (((IPropertyReferenceOperation)i.Target).Property.Name, Eval(i.Value)))], null);
            case IParameterReferenceOperation { Parameter: var parameter } when parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction }:
                return new Defaults([], parameter);
            case IObjectCreationOperation creation when creation.Type?.Name is "RouteValueDictionary" or "HttpRouteValueDictionary":
                if (creation.Arguments is [var values])
                {
                    return EvalDefaults(values.Value, depth);
                }

                return new Defaults([.. (creation.Initializer?.Initializers ?? []).OfType<IInvocationOperation>()
                    .Where(add => add.Arguments.Length == 2 && add.Arguments[0].Value.ConstantValue.Value is string)
                    .Select(add => ((string)add.Arguments[0].Value.ConstantValue.Value!, Eval(add.Arguments[1].Value)))], null);
            case ILocalReferenceOperation local when depth < MaxValueDepth && Initializer(local.Local) is { } initializer:
                return EvalDefaults(initializer, depth + 1);
            default:
                return Defaults.None;
        }
    }

    private Value Substitute(Value value, IMethodSymbol helper, IInvocationOperation call) =>
        Value.Concat([.. value.Parts.Select(part => part is ParameterPart { Parameter: var parameter } && Owns(helper, parameter)
            ? Eval(Argument(call.Arguments, parameter.Ordinal))
            : Value.Of(part))]);

    private Defaults Substitute(Defaults defaults, IMethodSymbol helper, IInvocationOperation call)
    {
        if (defaults.Parameter is { } parameter && Owns(helper, parameter))
        {
            return EvalDefaults(Argument(call.Arguments, parameter.Ordinal));
        }

        return defaults with { Entries = [.. defaults.Entries.Select(e => (e.Name, Substitute(e.Value, helper, call)))] };
    }

    private static bool Owns(IMethodSymbol helper, IParameterSymbol parameter) =>
        SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, helper.OriginalDefinition);

    /// <summary>A local's initializer, when the local is not assigned again in its method.</summary>
    private IOperation? Initializer(ILocalSymbol local)
    {
        if (local.IsRef || local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: var value } declarator
            || !_compilationOf.ContainsKey(declarator.SyntaxTree) || Body(declarator) is not { } body)
        {
            return null;
        }

        var model = Model(declarator.SyntaxTree);
        foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.ValueText == local.Name))
        {
            var written = identifier.Parent is AssignmentExpressionSyntax assignment && assignment.Left == identifier
                || identifier.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 };
            if (written && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, local))
            {
                return null;
            }
        }

        return model.GetOperation(value);
    }

    /// <summary>The expression a get-only static property returns, when it is one expression.</summary>
    private IOperation? Getter(IPropertySymbol property)
    {
        if (Source(property) is not IPropertySymbol source || source.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not PropertyDeclarationSyntax syntax)
        {
            return null;
        }

        var getter = syntax.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var expression = syntax.ExpressionBody?.Expression
            ?? getter?.ExpressionBody?.Expression
            ?? (getter?.Body?.Statements is [ReturnStatementSyntax { Expression: { } returned }] ? returned : null)
            ?? (getter is { Body: null, ExpressionBody: null } ? syntax.Initializer?.Value : null);
        return expression is null ? null : Model(expression.SyntaxTree).GetOperation(expression);
    }

    private IOperation? FieldInitializer(IFieldSymbol field) =>
        Source(field)?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: var value }
            ? Model(value.SyntaxTree).GetOperation(value)
            : null;

    /// <summary>The symbol as declared in one of the compilations' sources (a library's, for a metadata symbol), or null.</summary>
    private ISymbol? Source(ISymbol symbol)
    {
        if (symbol.DeclaringSyntaxReferences.FirstOrDefault() is { } reference && _compilationOf.ContainsKey(reference.SyntaxTree))
        {
            return symbol;
        }

        if (DocumentationCommentId.CreateDeclarationId(symbol.OriginalDefinition) is not { } id)
        {
            return null;
        }

        foreach (var compilation in _compilations.Where(c => string.Equals(c.AssemblyName, symbol.ContainingAssembly?.Name, StringComparison.Ordinal)))
        {
            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is { } found && found.DeclaringSyntaxReferences.FirstOrDefault() is { } declared
                && _compilationOf.ContainsKey(declared.SyntaxTree))
            {
                return found;
            }
        }

        return null;
    }

    // ---- Plumbing ----

    private IInvocationOperation? Operation(InvocationExpressionSyntax syntax) => Model(syntax.SyntaxTree).GetOperation(syntax) as IInvocationOperation;

    private SemanticModel Model(SyntaxTree tree)
    {
        if (!_models.TryGetValue(tree, out var model))
        {
            model = _compilationOf[tree].GetSemanticModel(tree);
            _models[tree] = model;
        }

        return model;
    }

    private INamedTypeSymbol? EnclosingType(IOperation operation) =>
        Model(operation.Syntax.SyntaxTree).GetEnclosingSymbol(operation.Syntax.SpanStart)?.ContainingType;

    /// <summary>Where a node is; for a call through a member access, where the member's name is (the line of <c>.AsActionFilterFor</c> in a chain).</summary>
    private Site SiteOf(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access })
        {
            node = access.Name;
        }

        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        return new Site(WebInventory.FileOf(_root, node.SyntaxTree) ?? "", position.Line + 1, position.Character);
    }

    private static SyntaxNode? Body(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax or LocalFunctionStatementSyntax && a is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax);

    private static IOperation? Argument(IInvocationOperation invocation, string parameter) => Argument(invocation.Arguments, parameter);

    private static IOperation? Argument(ImmutableArray<IArgumentOperation> arguments, string parameter) =>
        arguments.FirstOrDefault(a => a.Parameter?.Name == parameter)?.Value;

    private static IOperation? Argument(ImmutableArray<IArgumentOperation> arguments, int ordinal) =>
        arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal)?.Value;

    private static IOperation? Strip(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static IMethodSymbol Unreduced(IMethodSymbol method) => method.ReducedFrom ?? method;

    private static bool Is(ITypeSymbol? type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Name(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    private static string Strip(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;

    /// <summary>Where a registration is: file, line, and column, ordered in that order.</summary>
    private sealed record Site(string File, int Line, int Column) : IComparable<Site>
    {
        public int CompareTo(Site? other) =>
            other is null ? 1
            : string.CompareOrdinal(File, other.File) is var file and not 0 ? file
            : Line != other.Line ? Line.CompareTo(other.Line)
            : Column.CompareTo(other.Column);
    }

    private abstract record Part;

    private sealed record LiteralPart(string? Text) : Part;

    private sealed record ParameterPart(IParameterSymbol Parameter) : Part;

    private sealed record CodePart(string Text) : Part;

    /// <summary><c>UrlParameter.Optional</c> and <c>RouteParameter.Optional</c>.</summary>
    private sealed record OptionalPart : Part
    {
        public static readonly OptionalPart Instance = new();
    }

    /// <summary>A string as the code builds it: the parts are concatenated.</summary>
    private sealed record Value(ImmutableArray<Part> Parts)
    {
        public static readonly Value Null = new([new LiteralPart(null)]);

        public static Value Of(Part part) => new([part]);

        public static Value Literal(string? text) => Of(new LiteralPart(text));

        public static Value Code(string text) => Of(new CodePart(text));

        public static Value Concat(params Value[] values) => new([.. values.SelectMany(v => v.Parts)]);

        public bool IsLiteral => Parts.All(p => p is LiteralPart);

        public IEnumerable<IParameterSymbol> Parameters => Parts.OfType<ParameterPart>().Select(p => p.Parameter);

        /// <summary>The literal the parts make, when they are all literals (null when the value is null).</summary>
        public bool AsLiteral(out string? text)
        {
            text = null;
            if (!IsLiteral)
            {
                return false;
            }

            text = Parts is [LiteralPart { Text: null }] ? null : string.Concat(Parts.Cast<LiteralPart>().Select(p => p.Text));
            return true;
        }

        /// <summary>Parameters nobody resolved are code (their names).</summary>
        public Value Opaque() => new([.. Parts.Select(p => p is ParameterPart { Parameter: var parameter } ? new CodePart(parameter.Name) : p)]);

        /// <summary>The value as C#: literals quoted, adjacent literals joined, parts joined with <c>+</c>.</summary>
        public string Display()
        {
            var shown = new List<string>();
            string? pending = null;
            foreach (var part in Parts)
            {
                if (part is LiteralPart literal)
                {
                    pending = (pending ?? "") + literal.Text;
                    continue;
                }

                if (pending is not null)
                {
                    shown.Add(SymbolDisplay.FormatLiteral(pending, quote: true));
                    pending = null;
                }

                shown.Add(part switch
                {
                    CodePart code => code.Text,
                    ParameterPart parameter => parameter.Parameter.Name,
                    _ => "UrlParameter.Optional",
                });
            }

            if (pending is not null)
            {
                shown.Add(SymbolDisplay.FormatLiteral(pending, quote: true));
            }

            return string.Join(" + ", shown);
        }

        /// <summary>A default's value as the inventory lists it: the literal, <c>?</c> for optional, <c>null</c>, or the code.</summary>
        public string DefaultText() =>
            Parts is [OptionalPart] ? "?"
            : AsLiteral(out var text) ? text ?? "null"
            : Display();
    }

    /// <summary>Route defaults as entries, or the parameter they come from.</summary>
    private sealed record Defaults(ImmutableArray<(string Name, Value Value)> Entries, IParameterSymbol? Parameter)
    {
        public static readonly Defaults None = new([], null);

        public IEnumerable<IParameterSymbol> Parameters =>
            Entries.SelectMany(e => e.Value.Parameters).Concat(Parameter is null ? [] : [Parameter]);

        public Defaults Opaque() => new([.. Entries.Select(e => (e.Name, e.Value.Opaque()))], null);
    }
}
