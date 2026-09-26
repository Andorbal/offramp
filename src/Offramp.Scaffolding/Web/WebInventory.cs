using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Offramp.Analysis.Audits;
using Offramp.Core.Paths;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Web;

/// <summary>
/// <c>web inventory</c> (docs/spec/commands/scaffold.md#web-inventory): what an ASP.NET
/// (System.Web) application is made of, read with the semantic model from its recorded
/// compilation and from web.config. Read-only.
/// </summary>
public static class WebInventory
{
    private static readonly string[] HttpVerbs = ["Get", "Post", "Put", "Delete", "Patch", "Head", "Options"];

    /// <summary>Attributes that are routing, not filters.</summary>
    private static readonly HashSet<string> RoutingAttributes = new(StringComparer.Ordinal)
    {
        "Route", "RoutePrefix", "AcceptVerbs", "ActionName", "NonAction", "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions",
    };

    public static WebInventoryResult Analyze(string root, ProjectInfo project, Compilation compilation)
    {
        var directory = RepoPaths.Normalize(Path.GetDirectoryName(project.Id) ?? "");
        var trees = AuditEngine.Sources(compilation).Where(t => FileOf(root, t) is not null).OrderBy(t => t.FilePath, StringComparer.Ordinal).ToList();
        var types = trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Select(c => compilation.GetSemanticModel(t).GetDeclaredSymbol(c)))
            .OfType<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal)
            .ToList();

        var areas = types.Where(t => Derives(t, "System.Web.Mvc.AreaRegistration")).Select(t => AreaName(compilation, t)).OfType<string>().Order(StringComparer.Ordinal).ToList();
        var controllers = types.Select(t => Controller(root, compilation, t)).OfType<WebController>().ToList();
        var (routes, attributeRouting, globalFilters, bundles) = Registrations(root, compilation, trees);
        var config = WebConfig(root, directory);
        var modules = Components(root, types, "System.Web.IHttpModule", config?.Modules ?? []);
        var handlers = Components(root, types.Where(t => !Derives(t, "System.Web.UI.Page") && !Derives(t, "System.Web.HttpApplication")), "System.Web.IHttpHandler", config?.Handlers ?? []);
        var application = types.FirstOrDefault(t => Derives(t, "System.Web.HttpApplication"));
        var webForms = WebFormsFiles(root, directory);
        var kinds = new List<string>();
        if (controllers.Any(c => c.Kind == "mvc"))
        {
            kinds.Add("mvc");
        }

        if (controllers.Any(c => c.Kind == "webapi"))
        {
            kinds.Add("webapi");
        }

        if (webForms.Count > 0)
        {
            kinds.Add("webforms");
        }

        return new WebInventoryResult
        {
            Project = project.Id,
            Kind = kinds.Count == 0 ? "none" : string.Join('+', kinds),
            Url = IisUrl(root, project.Id),
            Controllers = controllers,
            Routes = routes,
            AttributeRouting = attributeRouting,
            GlobalFilters = globalFilters,
            Areas = areas,
            Modules = modules,
            Handlers = handlers,
            GlobalAsax = application is null ? [] : [.. application.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.Name.StartsWith("Application_", StringComparison.Ordinal) || m.Name.StartsWith("Session_", StringComparison.Ordinal))
                .OrderBy(m => m.Locations[0].SourceSpan.Start)
                .Select(m => m.Name)],
            Bundles = bundles,
            WebForms = webForms,
            Session = Session(root, compilation, trees),
            OutputCache = OutputCache(root, compilation, trees),
            Settings = config?.Settings ?? new WebSettings(),
            SystemWeb = Surface(root, compilation, trees),
        };
    }

    private static WebController? Controller(string root, Compilation compilation, INamedTypeSymbol type)
    {
        var kind = Derives(type, "System.Web.Mvc.Controller") ? "mvc" : Derives(type, "System.Web.Http.ApiController") ? "webapi" : null;
        if (kind is null || type.IsAbstract || type.DeclaringSyntaxReferences.Length == 0)
        {
            return null;
        }

        var prefix = Attributes(type).Where(a => Short(a) == "RoutePrefix").Select(FirstString).FirstOrDefault();
        var ns = type.ContainingNamespace.ToDisplayString();
        var area = ns.Contains(".Areas.", StringComparison.Ordinal) ? ns.Split(".Areas.")[1].Split('.')[0] : null;
        var actions = type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic
                && !m.GetAttributes().Any(a => Short(a) == "NonAction"))
            .OrderBy(m => m.Locations[0].SourceSpan.Start)
            .Select(m => Action(root, kind, prefix, m))
            .ToList();
        return new WebController
        {
            Name = type.Name,
            Type = type.ToDisplayString(),
            Kind = kind,
            Area = area,
            RoutePrefix = prefix,
            Filters = Filters(type),
            File = FileOf(root, type.DeclaringSyntaxReferences[0].SyntaxTree)!,
            Actions = actions,
        };
    }

    private static WebAction Action(string root, string kind, string? prefix, IMethodSymbol method)
    {
        var attributes = method.GetAttributes();
        var verbs = attributes.Select(Short).Where(n => n.StartsWith("Http", StringComparison.Ordinal) && RoutingAttributes.Contains(n)).Select(n => n[4..].ToUpperInvariant()).ToList();
        foreach (var accept in attributes.Where(a => Short(a) == "AcceptVerbs"))
        {
            verbs.AddRange(accept.ConstructorArguments.SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : [a])
                .Select(v => v.Value?.ToString()?.ToUpperInvariant()).OfType<string>());
        }

        if (verbs.Count == 0 && kind == "webapi" && HttpVerbs.FirstOrDefault(v => method.Name.StartsWith(v, StringComparison.Ordinal)) is { } convention)
        {
            verbs.Add(convention.ToUpperInvariant());
        }

        var routes = attributes.Where(a => Short(a) == "Route").Select(FirstString).OfType<string>().Select(t => Combine(prefix, t)).ToList();
        var location = method.Locations[0];
        return new WebAction
        {
            Name = method.Name,
            HttpMethods = [.. verbs.Distinct(StringComparer.Ordinal)],
            Routes = routes,
            Filters = Filters(method),
            File = FileOf(root, location.SourceTree!)!,
            Line = location.GetLineSpan().StartLinePosition.Line + 1,
        };
    }

    /// <summary>A route template under a prefix; <c>~/</c> escapes it.</summary>
    public static string Combine(string? prefix, string template) =>
        template.StartsWith("~/", StringComparison.Ordinal) ? template[2..]
        : string.IsNullOrEmpty(prefix) ? template
        : template.Length == 0 ? prefix
        : prefix.TrimEnd('/') + "/" + template;

    /// <summary>Convention routes, attribute routing switches, global filters, and bundles, in source order.</summary>
    private static (List<WebRoute> Routes, List<string> AttributeRouting, List<string> GlobalFilters, List<string> Bundles) Registrations(string root, Compilation compilation, List<SyntaxTree> trees)
    {
        var routes = new List<WebRoute>();
        var attributeRouting = new List<string>();
        var filters = new List<string>();
        var bundles = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var syntax in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetOperation(syntax) is not IInvocationOperation invocation)
                {
                    continue;
                }

                var method = invocation.TargetMethod;
                var container = method.ContainingType.ToDisplayString();
                var line = syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                switch (method.Name)
                {
                    case "MapRoute" when container is "System.Web.Mvc.RouteCollectionExtensions" or "System.Web.Mvc.AreaRegistrationContext":
                    case "MapHttpRoute" when container == "System.Web.Http.HttpRouteCollectionExtensions":
                    case "IgnoreRoute" when container == "System.Web.Mvc.RouteCollectionExtensions":
                        var area = container == "System.Web.Mvc.AreaRegistrationContext"
                            ? AreaName(compilation, model.GetEnclosingSymbol(syntax.SpanStart)?.ContainingType)
                            : null;
                        routes.Add(new WebRoute
                        {
                            Name = String(invocation, "name") ?? (method.Name == "IgnoreRoute" ? "(ignored)" : ""),
                            Template = String(invocation, "url") ?? String(invocation, "routeTemplate") ?? "",
                            Kind = method.Name switch { "MapHttpRoute" => "webapi", "IgnoreRoute" => "ignore", _ => "mvc" },
                            Area = area,
                            Defaults = Defaults(invocation),
                            File = FileOf(root, tree)!,
                            Line = line,
                        });
                        break;
                    case "MapMvcAttributeRoutes":
                        attributeRouting.Add("mvc");
                        break;
                    case "MapHttpAttributeRoutes":
                        attributeRouting.Add("webapi");
                        break;
                    case "Add" when container is "System.Web.Mvc.GlobalFilterCollection" or "System.Web.Http.Filters.HttpFilterCollection":
                        var argument = invocation.Arguments.FirstOrDefault()?.Value;
                        if ((argument is IConversionOperation conversion ? conversion.Operand : argument) is IObjectCreationOperation { Type: { } filter })
                        {
                            filters.Add(Strip(filter.Name));
                        }

                        break;
                    case "Add" when container == "System.Web.Optimization.BundleCollection":
                        if (invocation.Arguments.FirstOrDefault()?.Value.Descendants().OfType<IObjectCreationOperation>().FirstOrDefault()?.Arguments.FirstOrDefault()?.Value.ConstantValue.Value is string path)
                        {
                            bundles.Add(path);
                        }

                        break;
                }
            }
        }

        return (routes, [.. attributeRouting.Distinct(StringComparer.Ordinal)], filters, bundles);
    }

    private static string? String(IInvocationOperation invocation, string parameter) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameter)?.Value.ConstantValue.Value as string;

    /// <summary>An anonymous defaults object as <c>name = value</c>; <c>UrlParameter.Optional</c> and <c>RouteParameter.Optional</c> are <c>?</c>.</summary>
    private static List<string> Defaults(IInvocationOperation invocation)
    {
        var argument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "defaults")?.Value;
        var creation = argument is IConversionOperation conversion ? conversion.Operand : argument;
        if (creation is not IAnonymousObjectCreationOperation anonymous)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var initializer in anonymous.Initializers.OfType<ISimpleAssignmentOperation>())
        {
            if (initializer.Target is not IPropertyReferenceOperation property)
            {
                continue;
            }

            var value = initializer.Value is IConversionOperation inner ? inner.Operand : initializer.Value;
            var text = value.ConstantValue.HasValue ? value.ConstantValue.Value?.ToString() ?? "null"
                : value is IFieldReferenceOperation { Field.Name: "Optional" } ? "?"
                : value.Syntax.ToString();
            result.Add(property.Property.Name + " = " + text);
        }

        return result;
    }

    private static List<WebComponent> Components(string root, IEnumerable<INamedTypeSymbol> types, string contract, IReadOnlyList<(string Section, string Name, string Type, string? Path, string? Verb)> registrations)
    {
        var result = new Dictionary<string, WebComponent>(StringComparer.Ordinal);
        foreach (var type in types.Where(t => !t.IsAbstract && t.AllInterfaces.Any(i => i.ToDisplayString() == contract)))
        {
            var name = type.ToDisplayString();
            result[name] = new WebComponent { Name = type.Name, Type = name, File = FileOf(root, type.DeclaringSyntaxReferences[0].SyntaxTree) };
        }

        foreach (var (section, name, typeName, path, verb) in registrations)
        {
            var key = typeName.Split(',')[0].Trim();
            if (!result.TryGetValue(key, out var component))
            {
                if (key.StartsWith("System.", StringComparison.Ordinal))
                {
                    continue;
                }

                component = new WebComponent { Name = name, Type = key };
            }

            result[key] = component with
            {
                Name = name,
                Path = component.Path ?? path,
                Verb = component.Verb ?? verb,
                RegisteredIn = [.. component.RegisteredIn.Append(section).Distinct(StringComparer.Ordinal)],
            };
        }

        return [.. result.Values.OrderBy(c => c.Type, StringComparer.Ordinal)];
    }

    private static List<WebSite> Session(string root, Compilation compilation, List<SyntaxTree> trees)
    {
        var result = new SortedSet<(string File, int Line)>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var type = model.GetTypeInfo(name).Type?.ToDisplayString();
                if (type is "System.Web.HttpSessionStateBase" or "System.Web.SessionState.HttpSessionState")
                {
                    result.Add((FileOf(root, tree)!, name.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }
        }

        return [.. result.Select(r => new WebSite(r.File, r.Line, "Session"))];
    }

    private static List<WebSite> OutputCache(string root, Compilation compilation, List<SyntaxTree> trees)
    {
        var result = new List<WebSite>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var attribute in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
            {
                if (model.GetTypeInfo(attribute).Type?.ToDisplayString() == "System.Web.Mvc.OutputCacheAttribute")
                {
                    result.Add(new WebSite(FileOf(root, tree)!, attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1, attribute.ToString()));
                }
            }
        }

        return result;
    }

    /// <summary>The System.Web.* types each file uses.</summary>
    private static List<WebApiSurface> Surface(string root, Compilation compilation, List<SyntaxTree> trees)
    {
        var result = new List<WebApiSurface>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var used = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                for (var current = type; current is not null; current = current.ContainingType)
                {
                    if (current.ContainingNamespace.ToDisplayString() is var ns && (ns == "System.Web" || ns.StartsWith("System.Web.", StringComparison.Ordinal)))
                    {
                        used.Add(current.OriginalDefinition.ToDisplayString());
                        break;
                    }
                }
            }

            if (used.Count > 0)
            {
                result.Add(new WebApiSurface { File = FileOf(root, tree)!, Types = [.. used] });
            }
        }

        return result;
    }

    private sealed record Config(WebSettings Settings, List<(string Section, string Name, string Type, string? Path, string? Verb)> Modules, List<(string Section, string Name, string Type, string? Path, string? Verb)> Handlers);

    private static Config? WebConfig(string root, string directory)
    {
        var path = RepoPaths.ToAbsolute(root, directory.Length == 0 ? "Web.config" : directory + "/Web.config");
        if (!File.Exists(path))
        {
            return null;
        }

        var configuration = XDocument.Load(path).Root!;
        var web = configuration.Element("system.web");
        var server = configuration.Element("system.webServer");
        var modules = new List<(string, string, string, string?, string?)>();
        var handlers = new List<(string, string, string, string?, string?)>();
        foreach (var add in web?.Element("httpModules")?.Elements("add") ?? [])
        {
            modules.Add(("system.web", add.Attribute("name")?.Value ?? "", add.Attribute("type")?.Value ?? "", null, null));
        }

        foreach (var add in server?.Element("modules")?.Elements("add") ?? [])
        {
            modules.Add(("system.webServer", add.Attribute("name")?.Value ?? "", add.Attribute("type")?.Value ?? "", null, null));
        }

        foreach (var add in web?.Element("httpHandlers")?.Elements("add") ?? [])
        {
            handlers.Add(("system.web", add.Attribute("path")?.Value ?? "", add.Attribute("type")?.Value ?? "", add.Attribute("path")?.Value, add.Attribute("verb")?.Value));
        }

        foreach (var add in server?.Element("handlers")?.Elements("add") ?? [])
        {
            handlers.Add(("system.webServer", add.Attribute("name")?.Value ?? "", add.Attribute("type")?.Value ?? "", add.Attribute("path")?.Value, add.Attribute("verb")?.Value));
        }

        var settings = new WebSettings
        {
            Authentication = web?.Element("authentication")?.Attribute("mode")?.Value,
            LoginUrl = web?.Element("authentication")?.Element("forms")?.Attribute("loginUrl")?.Value,
            SessionState = web?.Element("sessionState")?.Attribute("mode")?.Value,
            MachineKey = web?.Element("machineKey") is not null,
            CustomErrors = web?.Element("customErrors")?.Attribute("mode")?.Value,
            Sections = [.. configuration.Elements().Select(e => e.Name.LocalName)],
        };
        return new Config(settings, modules, handlers);
    }

    private static List<string> WebFormsFiles(string root, string directory)
    {
        var absolute = RepoPaths.ToAbsolute(root, directory);
        return [.. Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".aspx" or ".ascx" or ".master" or ".ashx" or ".asmx")
            .Select(f => RepoPaths.ToRepositoryRelative(root, f))
            .Where(f => !f.Split('/').Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The IISUrl of the project's web settings (ProjectExtensions), else null.</summary>
    public static string? IisUrl(string root, string projectFile)
    {
        try
        {
            return XDocument.Load(RepoPaths.ToAbsolute(root, projectFile)).Descendants().FirstOrDefault(e => e.Name.LocalName == "IISUrl")?.Value.Trim() is { Length: > 0 } url ? url : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string? AreaName(Compilation compilation, INamedTypeSymbol? type)
    {
        var property = type?.GetMembers("AreaName").OfType<IPropertySymbol>().FirstOrDefault();
        var syntax = property?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
        var expression = syntax?.ExpressionBody?.Expression
            ?? syntax?.AccessorList?.Accessors.FirstOrDefault()?.ExpressionBody?.Expression
            ?? syntax?.AccessorList?.Accessors.FirstOrDefault()?.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
        return expression is null ? null : compilation.GetSemanticModel(expression.SyntaxTree).GetConstantValue(expression).Value as string;
    }

    private static List<string> Filters(ISymbol symbol) =>
        [.. symbol.DeclaringSyntaxReferences.SelectMany(r => r.GetSyntax() switch
            {
                MemberDeclarationSyntax member => member.AttributeLists.SelectMany(l => l.Attributes),
                _ => [],
            })
            .Where(a => !RoutingAttributes.Contains(Strip(a.Name.ToString().Split('.')[^1])))
            .Select(a => Strip(a.Name.ToString().Split('.')[^1]) + (a.ArgumentList is { Arguments.Count: > 0 } arguments ? arguments.ToString() : ""))];

    private static System.Collections.Immutable.ImmutableArray<AttributeData> Attributes(ISymbol symbol) => symbol.GetAttributes();

    private static string Short(AttributeData attribute) => Strip(attribute.AttributeClass?.Name ?? "");

    private static string? FirstString(AttributeData attribute) =>
        attribute.ConstructorArguments.FirstOrDefault().Value as string;

    private static string Strip(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;

    public static bool Derives(ITypeSymbol type, string baseName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseName)
            {
                return true;
            }
        }

        return false;
    }

    public static string? FileOf(string root, SyntaxTree tree)
    {
        if (!Path.IsPathRooted(tree.FilePath))
        {
            return null;
        }

        var relative = RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(tree.FilePath));
        return relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }
}
