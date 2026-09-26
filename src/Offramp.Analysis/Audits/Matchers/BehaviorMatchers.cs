using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>Invocations and object creations of a tree with the method they bind to.</summary>
internal static class Calls
{
    public static IEnumerable<(ExpressionSyntax Call, IMethodSymbol Method, SemanticModel Model)> In(AuditMatchContext context)
    {
        foreach (var tree in context.Trees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                    && AuditEngine.Bound(model, node) is IMethodSymbol method)
                {
                    yield return ((ExpressionSyntax)node, method.ReducedFrom ?? method, model);
                }
            }
        }
    }

    public static IReadOnlyList<ArgumentSyntax> Arguments(ExpressionSyntax call) => call switch
    {
        InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
        BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments ?? [],
        _ => [],
    };

    /// <summary>The name to report at: the invoked member's name, or the created type's.</summary>
    public static Location At(ExpressionSyntax call) => call switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name.GetLocation(),
        InvocationExpressionSyntax invocation => invocation.Expression.GetLocation(),
        ObjectCreationExpressionSyntax creation => creation.Type.GetLocation(),
        _ => call.GetLocation(),
    };

    public static bool Is(ITypeSymbol? type, string metadataName) =>
        type is not null && type.OriginalDefinition.ToDisplayString() == metadataName;

    public static bool HasParameterOfType(IMethodSymbol method, params string[] types) =>
        method.Parameters.Any(p => types.Contains(p.Type.OriginalDefinition.ToDisplayString(), StringComparer.Ordinal));

    /// <summary>The constant string an argument evaluates to, or null.</summary>
    public static string? Constant(SemanticModel model, ExpressionSyntax expression) =>
        model.GetConstantValue(expression) is { HasValue: true, Value: string text } ? text : null;

    public static RawFinding Finding(AuditRule rule, ExpressionSyntax call, IMethodSymbol method, string message, IReadOnlyDictionary<string, string>? details = null) =>
        new(rule, At(call), AuditEngine.Name(method), message, details) { Namespace = AuditEngine.NamespaceOf(method) };
}

/// <summary>
/// <c>OFR3101</c>: string comparisons and case mappings that use the current culture
/// implicitly: <c>string.Compare</c>/<c>CompareTo</c>, <c>IndexOf</c>/<c>LastIndexOf</c>,
/// <c>StartsWith</c>/<c>EndsWith</c> with a string, <c>ToUpper</c>/<c>ToLower</c> without a
/// culture, and ordering strings without a comparer.
/// </summary>
public sealed class CultureSensitiveStringMatcher : IAuditMatcher
{
    private static readonly string[] Explicit =
    [
        "System.StringComparison", "System.Globalization.CultureInfo", "System.IFormatProvider",
        "System.Globalization.CompareOptions", "System.Collections.Generic.IComparer<T>",
    ];

    public string Name => "culture-sensitive-string";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3101") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, _) in Calls.In(context))
        {
            if (Calls.HasParameterOfType(method, Explicit))
            {
                continue;
            }

            if (IsCultureSensitiveStringMethod(method) || IsStringOrdering(method))
            {
                yield return Calls.Finding(rule, call, method, $"{AuditEngine.Name(method)} uses the current culture; the result differs between NLS (.NET Framework on Windows) and ICU.");
            }
        }
    }

    private static bool IsCultureSensitiveStringMethod(IMethodSymbol method)
    {
        if (method.ContainingType?.SpecialType != SpecialType.System_String)
        {
            return false;
        }

        var stringArgument = method.Parameters.Length > 0 && method.Parameters[0].Type.SpecialType == SpecialType.System_String;
        return method.Name switch
        {
            "Compare" or "CompareTo" => true,
            "IndexOf" or "LastIndexOf" or "StartsWith" or "EndsWith" => stringArgument,
            "ToUpper" or "ToLower" => method.Parameters.Length == 0,
            _ => false,
        };
    }

    /// <summary><c>OrderBy</c>/<c>ThenBy</c> (and descending) whose key is a string, without a comparer.</summary>
    private static bool IsStringOrdering(IMethodSymbol method) =>
        method.ContainingType?.ToDisplayString() is "System.Linq.Enumerable" or "System.Linq.Queryable"
        && method.Name is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending"
        && method.TypeArguments.Length == 2
        && method.TypeArguments[1].SpecialType == SpecialType.System_String;
}

/// <summary>
/// <c>OFR3102</c>: <c>Encoding.GetEncoding</c> for a code page modern .NET does not provide
/// without <c>CodePagesEncodingProvider</c>. Unicode, ASCII, and Latin-1 are built in; a
/// project that registers a provider anywhere is left alone.
/// </summary>
public sealed class EncodingCodePageMatcher : IAuditMatcher
{
    private static readonly HashSet<int> BuiltinPages = [1200, 1201, 12000, 12001, 20127, 28591, 65000, 65001];

    private static readonly HashSet<string> BuiltinNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf-8", "utf8", "utf-16", "utf-16le", "utf-16be", "unicode", "unicodefffe", "utf-32", "utf-32le", "utf-32be",
        "us-ascii", "ascii", "iso-8859-1", "latin1", "utf-7",
    };

    public string Name => "encoding-code-page";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3102") is not { } rule)
        {
            yield break;
        }

        var calls = Calls.In(context).ToList();
        if (calls.Any(c => c.Method.Name == "RegisterProvider" && Calls.Is(c.Method.ContainingType, "System.Text.Encoding")))
        {
            yield break;
        }

        foreach (var (call, method, model) in calls)
        {
            if (method.Name != "GetEncoding" || !Calls.Is(method.ContainingType, "System.Text.Encoding") || Calls.Arguments(call) is not [var first, ..])
            {
                continue;
            }

            var value = model.GetConstantValue(first.Expression);
            var builtin = value.Value switch
            {
                int page => BuiltinPages.Contains(page),
                string name => BuiltinNames.Contains(name),
                _ => false,
            };
            if (!builtin)
            {
                var page = value.HasValue ? Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) : "a computed code page";
                yield return Calls.Finding(rule, call, method, $"Encoding.GetEncoding({page}) needs CodePagesEncodingProvider.Instance registered on modern .NET.",
                    new SortedDictionary<string, string>(StringComparer.Ordinal) { ["codePage"] = page ?? "" });
            }
        }
    }
}

/// <summary>
/// <c>OFR3103</c>: constant paths with a backslash or a drive letter passed to <c>System.IO</c>
/// APIs (the rule's symbols cover Windows-only special folders).
/// </summary>
public sealed class WindowsPathMatcher : IAuditMatcher
{
    public string Name => "windows-path";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3103") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, model) in Calls.In(context))
        {
            if (method.ContainingNamespace?.ToDisplayString() != "System.IO")
            {
                continue;
            }

            foreach (var argument in Calls.Arguments(call))
            {
                if (WindowsPath(model, argument.Expression) is { } path)
                {
                    yield return Calls.Finding(rule, call, method, $"{AuditEngine.Name(method)} receives the Windows path \"{path}\".",
                        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["path"] = path });
                    break;
                }
            }
        }
    }

    private static string? WindowsPath(SemanticModel model, ExpressionSyntax expression)
    {
        var text = Calls.Constant(model, expression)
            ?? (expression is InterpolatedStringExpressionSyntax interpolated
                ? string.Concat(interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText))
                : null);
        if (text is null)
        {
            return null;
        }

        var driveLetter = text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':';
        return driveLetter || text.Contains('\\', StringComparison.Ordinal) ? text : null;
    }
}

/// <summary><c>OFR3104</c>: <c>TimeZoneInfo.FindSystemTimeZoneById</c> with a Windows ID (or an ID from data).</summary>
public sealed class WindowsTimeZoneMatcher : IAuditMatcher
{
    public string Name => "windows-time-zone";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3104") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, model) in Calls.In(context))
        {
            if (method.Name != "FindSystemTimeZoneById" || !Calls.Is(method.ContainingType, "System.TimeZoneInfo") || Calls.Arguments(call) is not [var id, ..])
            {
                continue;
            }

            var constant = Calls.Constant(model, id.Expression);
            if (constant is null)
            {
                yield return Calls.Finding(rule, call, method, "FindSystemTimeZoneById receives an ID from data; Windows IDs need conversion where ICU is unavailable.");
            }
            else if (!constant.Contains('/', StringComparison.Ordinal) && constant is not ("UTC" or "GMT"))
            {
                yield return Calls.Finding(rule, call, method, $"\"{constant}\" is a Windows time zone ID; other platforms use IANA IDs.",
                    new SortedDictionary<string, string>(StringComparer.Ordinal) { ["timeZone"] = constant });
            }
        }
    }
}

/// <summary><c>OFR3109</c>: <c>double</c>/<c>float</c> <c>ToString</c> without a format.</summary>
public sealed class FloatingPointToStringMatcher : IAuditMatcher
{
    public string Name => "floating-point-to-string";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3109") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, _) in Calls.In(context))
        {
            var floating = method.ContainingType?.SpecialType is SpecialType.System_Double or SpecialType.System_Single;
            if (floating && method.Name == "ToString" && !method.Parameters.Any(p => p.Type.SpecialType == SpecialType.System_String))
            {
                yield return Calls.Finding(rule, call, method, $"{method.ContainingType!.Name}.ToString() without a format gives the shortest round-trippable string on .NET Core 3.0 and later.");
            }
        }
    }
}

/// <summary><c>OFR3113</c>: a <c>Regex</c> constructed or applied without a match timeout.</summary>
public sealed class RegexWithoutTimeoutMatcher : IAuditMatcher
{
    private static readonly HashSet<string> StaticApplications = new(StringComparer.Ordinal) { "IsMatch", "Match", "Matches", "Replace", "Split", "Count", "EnumerateMatches" };

    public string Name => "regex-without-timeout";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3113") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, _) in Calls.In(context))
        {
            if (!Calls.Is(method.ContainingType, "System.Text.RegularExpressions.Regex") || Calls.HasParameterOfType(method, "System.TimeSpan"))
            {
                continue;
            }

            if (method.MethodKind == MethodKind.Constructor || (method.IsStatic && StaticApplications.Contains(method.Name)))
            {
                yield return Calls.Finding(rule, call, method, "The regular expression has no match timeout.");
            }
        }
    }
}

/// <summary><c>OFR3003</c> (in part): <c>BeginInvoke</c>/<c>EndInvoke</c> on a delegate throw on modern .NET.</summary>
public sealed class DelegateBeginInvokeMatcher : IAuditMatcher
{
    public string Name => "delegate-begin-invoke";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3003") is not { } rule)
        {
            yield break;
        }

        foreach (var (call, method, _) in Calls.In(context))
        {
            if (method.ContainingType?.TypeKind == TypeKind.Delegate && method.Name is "BeginInvoke" or "EndInvoke")
            {
                yield return Calls.Finding(rule, call, method, $"Delegate {method.Name} throws PlatformNotSupportedException on modern .NET; use Task.Run.");
            }
        }
    }
}
