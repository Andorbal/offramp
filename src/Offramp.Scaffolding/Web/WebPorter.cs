using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Offramp.Scaffolding.Web;

/// <summary>A controller as the scaffolded project will have it: its members as text, the actions marked.</summary>
internal sealed class PortedControllerSource
{
    public required INamedTypeSymbol Type { get; init; }

    public required string Kind { get; init; }

    public required string SourceFile { get; init; }

    public required List<string> Usings { get; init; }

    public required List<string> ClassAttributes { get; init; }

    /// <summary>Members in source order: (action name or null for a helper member, text, the original declaration).</summary>
    public required List<(string? Action, string Text, MemberDeclarationSyntax Syntax)> Members { get; init; }

    public List<WebUnported> Unported { get; } = [];

    public List<string> Notes { get; } = [];

    public HashSet<string> Dropped { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> Ported => Members.Where(m => m.Action is not null && !Dropped.Contains(m.Action)).Select(m => m.Action!).Distinct(StringComparer.Ordinal);

    /// <summary>Whether a ported action needs an authenticated user ([Authorize] on it or the class).</summary>
    public bool Authorizes => Ported.Any()
        && (ClassAttributes.Any(a => a.StartsWith("Authorize", StringComparison.Ordinal))
            || Members.Any(m => (m.Action is null || !Dropped.Contains(m.Action)) && m.Text.Contains("[Authorize", StringComparison.Ordinal)));
}

/// <summary>
/// Ports MVC and Web API controllers to ASP.NET Core
/// (docs/spec/commands/scaffold.md#web-scaffold): actions that render views or use
/// System.Web beyond what maps one to one stay with the legacy application; the rest are
/// copied as text with the mapped names replaced (IHttpActionResult, HttpNotFound,
/// Json(x, JsonRequestBehavior), RoutePrefix, OutputCache), so their code keeps its
/// formatting. The compiler has the last word: <see cref="WebScaffolder"/> compiles the
/// result and leaves the actions it rejects to the legacy application too.
/// </summary>
internal static class WebPorter
{
    /// <summary>Controller and ApiController members with an ASP.NET Core counterpart of the same shape.</summary>
    private static readonly HashSet<string> MappedMembers = new(StringComparer.Ordinal)
    {
        "Ok", "NotFound", "BadRequest", "Json", "Content", "Redirect", "RedirectToAction", "RedirectToRoute", "Created",
        "HttpNotFound", "StatusCode", "Conflict", "InternalServerError", "Unauthorized", "ModelState", "File",
    };

    private static readonly HashSet<string> MappedTypes = new(StringComparer.Ordinal)
    {
        "System.Web.Mvc.Controller", "System.Web.Http.ApiController", "System.Web.Mvc.ActionResult", "System.Web.Mvc.JsonResult",
        "System.Web.Mvc.ContentResult", "System.Web.Http.IHttpActionResult", "System.Web.Mvc.HttpStatusCodeResult",
        "System.Web.Mvc.JsonRequestBehavior", "System.Web.Mvc.ModelStateDictionary", "System.Web.Http.ModelBinding.ModelStateDictionary",
        "System.Web.Mvc.ControllerBase",
    };

    /// <summary>Attribute → its ASP.NET Core name, or null to leave it out.</summary>
    private static readonly Dictionary<string, string?> Attributes = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "HttpGet", ["HttpPost"] = "HttpPost", ["HttpPut"] = "HttpPut", ["HttpDelete"] = "HttpDelete", ["HttpPatch"] = "HttpPatch",
        ["HttpHead"] = "HttpHead", ["HttpOptions"] = "HttpOptions", ["AcceptVerbs"] = "AcceptVerbs", ["Route"] = "Route", ["RoutePrefix"] = "Route",
        ["ActionName"] = "ActionName", ["NonAction"] = "NonAction", ["Authorize"] = "Authorize", ["AllowAnonymous"] = "AllowAnonymous",
        ["ValidateAntiForgeryToken"] = "ValidateAntiForgeryToken", ["OutputCache"] = "ResponseCache", ["HandleError"] = null,
        ["FromBody"] = "FromBody", ["FromUri"] = "FromQuery",
    };

    public static PortedControllerSource Port(Compilation compilation, INamedTypeSymbol type, string kind, string sourceFile)
    {
        var declaration = (ClassDeclarationSyntax)type.DeclaringSyntaxReferences[0].GetSyntax();
        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        var usings = declaration.SyntaxTree.GetCompilationUnitRoot().Usings
            .Select(u => u.Name?.ToString() ?? "")
            .Where(n => n.Length > 0 && n != "System.Web" && !n.StartsWith("System.Web.", StringComparison.Ordinal))
            .ToList();
        var result = new PortedControllerSource
        {
            Type = type,
            Kind = kind,
            SourceFile = sourceFile,
            Usings = usings,
            ClassAttributes = [],
            Members = [],
        };

        var hasRoute = false;
        foreach (var attribute in declaration.AttributeLists.SelectMany(l => l.Attributes))
        {
            var name = Name(attribute);
            if (Attributes.TryGetValue(name, out var mapped) && mapped is not null)
            {
                result.ClassAttributes.Add(mapped + (attribute.ArgumentList?.ToString() ?? ""));
                hasRoute |= mapped == "Route";
            }
            else
            {
                result.Notes.Add($"[{attribute}] is left out: it has no ASP.NET Core attribute of the same meaning.");
            }
        }

        var conventional = kind == "webapi" && !hasRoute;
        if (conventional)
        {
            result.ClassAttributes.Add("Route(\"api/[controller]\")");
        }

        foreach (var member in declaration.Members)
        {
            var symbol = model.GetDeclaredSymbol(member);
            var action = member is MethodDeclarationSyntax method && symbol is IMethodSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false, MethodKind: MethodKind.Ordinary }
                && !method.AttributeLists.SelectMany(l => l.Attributes).Any(a => Name(a) == "NonAction")
                ? method.Identifier.ValueText
                : null;
            if (WhyNot(model, member, kind) is { } reason)
            {
                if (action is not null)
                {
                    result.Unported.Add(new WebUnported(action, reason));
                }

                continue;
            }

            var text = Rewrite(model, member, kind);
            if (kind == "webapi" && member is MethodDeclarationSyntax apiAction && action is not null)
            {
                text = WebApiVerb(apiAction, text, conventional);
            }

            result.Members.Add((action, text, member));
        }

        return result;
    }

    /// <summary>The controller's file for the new project, with the dropped actions left out.</summary>
    public static string Render(PortedControllerSource controller, string header, out Dictionary<string, TextSpan> actionSpans)
    {
        actionSpans = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.Append(header);
        var usings = controller.Usings.Append("Microsoft.AspNetCore.Mvc");
        if (controller.ClassAttributes.Any(a => a.StartsWith("Authorize", StringComparison.Ordinal) || a.StartsWith("AllowAnonymous", StringComparison.Ordinal))
            || controller.Members.Any(m => m.Text.Contains("[Authorize", StringComparison.Ordinal) || m.Text.Contains("[AllowAnonymous", StringComparison.Ordinal)))
        {
            usings = usings.Append("Microsoft.AspNetCore.Authorization");
        }

        foreach (var name in usings.Distinct(StringComparer.Ordinal).OrderBy(u => u.StartsWith("System", StringComparison.Ordinal) ? 0 : 1).ThenBy(u => u, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"using {name};\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"\nnamespace {controller.Type.ContainingNamespace.ToDisplayString()}\n{{\n");
        if (controller.Kind == "webapi")
        {
            builder.Append("    [ApiController]\n");
        }

        foreach (var attribute in controller.ClassAttributes)
        {
            builder.Append(CultureInfo.InvariantCulture, $"    [{attribute}]\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"    public class {controller.Type.Name} : {(controller.Kind == "webapi" ? "ControllerBase" : "Controller")}\n    {{\n");
        var first = true;
        foreach (var (action, text, _) in controller.Members)
        {
            if (action is not null && controller.Dropped.Contains(action))
            {
                continue;
            }

            builder.Append(first ? "" : "\n");
            first = false;
            var start = builder.Length;
            builder.Append(text.TrimEnd()).Append('\n');
            if (action is not null)
            {
                var span = TextSpan.FromBounds(start, builder.Length);
                actionSpans[action] = actionSpans.TryGetValue(action, out var existing) ? TextSpan.FromBounds(Math.Min(existing.Start, span.Start), Math.Max(existing.End, span.End)) : span;
            }
        }

        builder.Append("    }\n}\n");
        return builder.ToString();
    }

    /// <summary>Why a member cannot be ported as it is, or null.</summary>
    private static string? WhyNot(SemanticModel model, MemberDeclarationSyntax member, string kind)
    {
        foreach (var name in member.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (symbol is null || name.Parent is AttributeSyntax || name.Ancestors().Any(a => a is AttributeSyntax))
            {
                continue;
            }

            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            var ns = type?.ContainingNamespace.ToDisplayString() ?? "";
            if (ns != "System.Web" && !ns.StartsWith("System.Web.", StringComparison.Ordinal))
            {
                continue;
            }

            if (symbol.Name is "View" or "PartialView" && symbol is IMethodSymbol)
            {
                return "renders a Razor view; views are not ported, so the legacy application serves it.";
            }

            var definition = type!.OriginalDefinition.ToDisplayString();
            if (symbol is INamedTypeSymbol && (MappedTypes.Contains(definition) || name.Parent is MemberAccessExpressionSyntax access && access.Expression == name))
            {
                // A type in front of a member access: the member says more (HttpContext.Current).
                continue;
            }

            if (symbol is not INamedTypeSymbol && (MappedTypes.Contains(definition) || type.Name == "JsonRequestBehavior") && (MappedMembers.Contains(symbol.Name) || type.Name == "JsonRequestBehavior"))
            {
                continue;
            }

            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && definition == "System.Web.Mvc.HttpStatusCodeResult")
            {
                continue;
            }

            if (kind == "webapi" && symbol.Name == "Json" && symbol is IMethodSymbol)
            {
                continue;
            }

            var what = symbol is INamedTypeSymbol ? definition : $"{type.Name}.{symbol.Name}";
            return $"uses {what} (System.Web), which has no one-to-one ASP.NET Core counterpart.";
        }

        return null;
    }

    /// <summary>The member's text with the mapped names replaced.</summary>
    private static string Rewrite(SemanticModel model, MemberDeclarationSyntax member, string kind)
    {
        var edits = new List<TextChange>();
        foreach (var attribute in member.DescendantNodes().OfType<AttributeListSyntax>().Where(l => l.Parent == member || l.Parent is ParameterSyntax).SelectMany(l => l.Attributes))
        {
            var name = Name(attribute);
            if (Attributes.TryGetValue(name, out var mapped))
            {
                if (mapped is null)
                {
                    var list = (AttributeListSyntax)attribute.Parent!;
                    edits.Add(new TextChange(list.Attributes.Count == 1 ? list.FullSpan : attribute.Span, list.Attributes.Count == 1 ? "" : ""));
                }
                else if (mapped != name)
                {
                    edits.Add(new TextChange(attribute.Name.Span, mapped));
                }
            }
        }

        foreach (var node in member.DescendantNodes())
        {
            switch (node)
            {
                case IdentifierNameSyntax { Identifier.ValueText: "IHttpActionResult" } identifier when model.GetSymbolInfo(identifier).Symbol is INamedTypeSymbol:
                    edits.Add(new TextChange(identifier.Span, "IActionResult"));
                    break;
                case InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method && Namespace(method).StartsWith("System.Web", StringComparison.Ordinal):
                    var target = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name : invocation.Expression as SimpleNameSyntax;
                    switch (method.Name)
                    {
                        case "HttpNotFound" when target is not null:
                            edits.Add(new TextChange(target.Identifier.Span, "NotFound"));
                            break;
                        case "InternalServerError" when invocation.ArgumentList.Arguments.Count == 0:
                            edits.Add(new TextChange(invocation.Span, "StatusCode(500)"));
                            break;
                        case "Json" when kind == "mvc" && invocation.ArgumentList.Arguments.Count == 2
                            && model.GetTypeInfo(invocation.ArgumentList.Arguments[1].Expression).Type?.Name == "JsonRequestBehavior":
                            edits.Add(new TextChange(TextSpan.FromBounds(invocation.ArgumentList.Arguments[0].Span.End, invocation.ArgumentList.Arguments[1].Span.End), ""));
                            break;
                        case "Json" when kind == "webapi" && invocation.ArgumentList.Arguments.Count == 1:
                            edits.Add(new TextChange(invocation.Span, "new JsonResult(" + invocation.ArgumentList.Arguments[0] + ")"));
                            break;
                    }

                    break;
                case ObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation).Type?.ToDisplayString() == "System.Web.Mvc.HttpStatusCodeResult" && creation.ArgumentList is { Arguments.Count: > 0 } arguments:
                    edits.Add(new TextChange(creation.Span, "StatusCode((int)(" + arguments.Arguments[0] + "))"));
                    break;
            }
        }

        var text = member.SyntaxTree.GetText().GetSubText(member.FullSpan);
        var offset = member.FullSpan.Start;
        var local = edits
            .GroupBy(e => e.Span.Start)
            .Select(g => g.OrderByDescending(e => e.Span.Length).First())
            .OrderBy(e => e.Span.Start)
            .Aggregate(new List<TextChange>(), (accepted, edit) =>
            {
                if (accepted.Count == 0 || edit.Span.Start >= accepted[^1].Span.End)
                {
                    accepted.Add(edit);
                }

                return accepted;
            })
            .Select(e => new TextChange(new TextSpan(e.Span.Start - offset, e.Span.Length), e.NewText!));
        return Reindent(text.WithChanges(local).ToString());
    }

    private static readonly string[] ConventionVerbs = ["Get", "Post", "Put", "Delete", "Patch", "Head", "Options"];

    /// <summary>
    /// Web API's verb convention made explicit: the verb from the name's prefix (POST when there
    /// is none), <c>{id}</c> when a conventionally routed action takes an id. ASP.NET Core does
    /// not infer verbs from names.
    /// </summary>
    private static string WebApiVerb(MethodDeclarationSyntax method, string text, bool conventional)
    {
        var attributes = method.AttributeLists.SelectMany(l => l.Attributes).Select(Name).ToList();
        if (attributes.Any(a => a.StartsWith("Http", StringComparison.Ordinal) || a == "AcceptVerbs"))
        {
            return text;
        }

        var verb = ConventionVerbs.FirstOrDefault(v => method.Identifier.ValueText.StartsWith(v, StringComparison.Ordinal)) ?? "Post";
        var template = conventional && !attributes.Contains("Route") && method.ParameterList.Parameters.Any(p => p.Identifier.ValueText == "id") ? "(\"{id}\")" : "";
        return InsertAttribute(text, $"Http{verb}{template}");
    }

    /// <summary>Adds an attribute line after the member's comments, at its indentation.</summary>
    private static string InsertAttribute(string text, string attribute)
    {
        var lines = text.TrimStart('\n').Split('\n').ToList();
        var index = lines.FindIndex(l => l.Trim() is { Length: > 0 } t && !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("/*", StringComparison.Ordinal) && !t.StartsWith('*') && !t.StartsWith('#'));
        index = index < 0 ? 0 : index;
        var lead = lines[index][..(lines[index].Length - lines[index].TrimStart(' ').Length)];
        lines.Insert(index, $"{lead}[{attribute}]");
        return string.Join('\n', lines);
    }

    /// <summary>Members keep their own indentation (8 spaces in a namespaced class), without leading blank lines.</summary>
    private static string Reindent(string text) => text.TrimStart('\r', '\n');

    private static string Name(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.')[^1];
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static string Namespace(ISymbol symbol) => symbol.ContainingType?.ContainingNamespace.ToDisplayString() ?? "";
}
