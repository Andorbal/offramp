using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using NuGet.Versioning;
using Offramp.Analysis.Audits;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>A finding before severity overrides: the rule, the case, where, and what.</summary>
internal sealed record RawNewtonsoftFinding(NewtonsoftRule Rule, NewtonsoftCase? Case, Location Location, string Symbol, string Message, IReadOnlyDictionary<string, string> Details);

/// <summary>What the detectors of <c>rules/newtonsoft-breaking.yml</c> look at for one project.</summary>
internal sealed record DetectorContext(
    Compilation Compilation,
    IReadOnlyList<SyntaxTree> Trees,
    IReadOnlyList<FoundSite> Sites,
    SettingsShape? DefaultSettings,
    NuGetVersion Current,
    NuGetVersion Target);

/// <summary>
/// The matchers named by the rules' <c>detector</c>. Each case runs only when its label for the
/// version in use differs from its label for the target (ADR 0068).
/// </summary>
internal static class NewtonsoftDetectors
{
    private const string CamelResolver = "Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver";
    private const string CamelStrategy = "Newtonsoft.Json.Serialization.CamelCaseNamingStrategy";

    public static IEnumerable<RawNewtonsoftFinding> Run(DetectorContext context)
    {
        foreach (var rule in NewtonsoftRules.All)
        {
            foreach (var @case in rule.Cases.Where(c => c.Differs(context.Current, context.Target)))
            {
                var found = rule.Detector switch
                {
                    "camel-case-names" => CamelCaseNames(context, rule, @case),
                    "iserializable-without-serializable" => PayloadTypes(context, rule, @case, null, IsSerializableWithoutAttribute, "implements ISerializable without [Serializable]"),
                    "enum-member-dictionary-keys" => PayloadMembers(context, rule, @case, null, EnumMemberKeys, "is a dictionary keyed by an enum with [EnumMember]"),
                    "decimal-members" => PayloadMembers(context, rule, @case, SiteDirection.Deserialize, IsDecimal, "is a decimal read from JSON"),
                    "caught-exceptions" => CaughtExceptions(context, rule, @case),
                    "string-enum-converter-integers" => StrictEnumConverters(context, rule, @case),
                    "recursive-payloads" => RecursivePayloads(context, rule, @case),
                    "to-object-dates" => ToObjectDates(context, rule, @case),
                    "data-contract-collections" => PayloadTypes(context, rule, @case, null, IsDataContractCollection, "is a [DataContract] collection"),
                    "exception-payloads" => PayloadTypes(context, rule, @case, null, IsExceptionWithoutAttribute, "is an exception without [Serializable]"),
                    "immutable-interfaces" => PayloadMembers(context, rule, @case, SiteDirection.Deserialize, IsImmutableInterface, "is an immutable set or dictionary interface"),
                    "data-dependent" => DataDependent(context, rule, @case),
                    var other => throw new InvalidDataException($"{rule.Id} names an unknown detector '{other}'."),
                };
                foreach (var finding in found)
                {
                    yield return finding;
                }
            }
        }
    }

    /// <summary>A finding per payload member whose JSON name camel-cases differently, at the first site that writes or reads it.</summary>
    private static IEnumerable<RawNewtonsoftFinding> CamelCaseNames(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case)
    {
        var current = @case.LabelFor(context.Current);
        var target = @case.LabelFor(context.Target);
        var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var dataDependent = NewtonsoftRules.All.Single(r => r.Detector == "data-dependent");
        foreach (var site in Sites(context, null).Where(s => Shape(context, s) is { } shape && (shape.Creates(CamelResolver) || shape.Creates(CamelStrategy))))
        {
            foreach (var member in PayloadClosure.Of(site.Payloads).Members.Where(m => reported.Add(m.Member)))
            {
                var (old, @new) = (CamelCase.For(current, member.JsonName), CamelCase.For(target, member.JsonName));
                if (old != @new)
                {
                    yield return Finding(rule, @case, site.Call.Syntax.GetLocation(), AuditEngine.Name(member.Member),
                        $"{AuditEngine.Name(member.Member)} is written as '{old}' and becomes '{@new}' on the target (camel case, site {site.Site.Id}).",
                        ("site", site.Site.Id), ("name", member.JsonName), ("old", old), ("new", @new));
                }
                else if (PayloadClosure.Dictionary(member.Type) is { } dictionary && dictionary.TypeArguments[0].SpecialType == SpecialType.System_String)
                {
                    yield return Finding(dataDependent, @case, site.Call.Syntax.GetLocation(), AuditEngine.Name(member.Member),
                        $"Site {site.Site.Id} camel-cases the keys of {AuditEngine.Name(member.Member)}; keys with spaces after capitals change on the target ('URL VALUE' was '{CamelCase.For(current, "URL VALUE")}', becomes '{CamelCase.For(target, "URL VALUE")}').",
                        ("site", site.Site.Id));
                }
            }
        }
    }

    /// <summary>A finding per payload type that matches, at the first site that carries it.</summary>
    private static IEnumerable<RawNewtonsoftFinding> PayloadTypes(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case,
        SiteDirection? direction, Func<ITypeSymbol, bool> matches, string what)
    {
        var reported = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var site in Sites(context, direction))
        {
            foreach (var type in PayloadClosure.Of(site.Payloads).Types.Where(matches).Where(reported.Add))
            {
                yield return Finding(rule, @case, site.Call.Syntax.GetLocation(), AuditEngine.Name(type),
                    $"{AuditEngine.Name(type)} {what}, and site {site.Site.Id} carries it.",
                    ("site", site.Site.Id), ("type", AuditEngine.Name(type)));
            }
        }
    }

    /// <summary>A finding per payload member (or root payload) whose type matches, at the first site that carries it.</summary>
    private static IEnumerable<RawNewtonsoftFinding> PayloadMembers(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case,
        SiteDirection? direction, Func<ITypeSymbol, bool> matches, string what)
    {
        var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var site in Sites(context, direction))
        {
            foreach (var root in site.Payloads.Where(matches).Where(r => reported.Add(r)))
            {
                yield return Finding(rule, @case, site.Call.Syntax.GetLocation(), AuditEngine.Name(root),
                    $"Site {site.Site.Id}'s payload {AuditEngine.Name(root)} {what}.",
                    ("site", site.Site.Id), ("type", AuditEngine.Name(root)));
            }

            foreach (var member in PayloadClosure.Of(site.Payloads).Members.Where(m => matches(m.Type)).Where(m => reported.Add(m.Member)))
            {
                yield return Finding(rule, @case, site.Call.Syntax.GetLocation(), AuditEngine.Name(member.Member),
                    $"{AuditEngine.Name(member.Member)} {what}, and site {site.Site.Id} carries it.",
                    ("site", site.Site.Id), ("member", AuditEngine.Name(member.Member)));
            }
        }
    }

    private static IEnumerable<RawNewtonsoftFinding> CaughtExceptions(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case)
    {
        foreach (var tree in context.Trees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var attempt in tree.GetRoot().DescendantNodes().OfType<TryStatementSyntax>())
            {
                var call = attempt.Block.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Select(i => model.GetOperation(i) as IInvocationOperation)
                    .FirstOrDefault(i => i is not null && NewtonsoftApis.InNewtonsoft(i.TargetMethod) && @case.Apis.Contains(NewtonsoftApis.Key(i.TargetMethod), StringComparer.Ordinal));
                if (call is null)
                {
                    continue;
                }

                foreach (var clause in attempt.Catches.Where(c => c.Declaration is { } d && model.GetTypeInfo(d.Type).Type?.GetDocumentationCommentId() == @case.Caught))
                {
                    yield return Finding(rule, @case, clause.Declaration!.Type.GetLocation(), NewtonsoftApis.Key(call.TargetMethod),
                        $"This catches {@case.Caught![2..]} from {AuditEngine.Name(call.TargetMethod)}, which throws {@case.Thrown![2..]} on the target.",
                        ("caught", @case.Caught), ("thrown", @case.Thrown));
                }
            }
        }
    }

    private static IEnumerable<RawNewtonsoftFinding> StrictEnumConverters(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case)
    {
        foreach (var tree in context.Trees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (model.GetOperation(assignment) is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property: { Name: "AllowIntegerValues" } property }, Value.ConstantValue: { HasValue: true, Value: false } }
                    && property.ContainingType.ToDisplayString() == "Newtonsoft.Json.Converters.StringEnumConverter")
                {
                    yield return Finding(rule, @case, assignment.GetLocation(), "Newtonsoft.Json.Converters.StringEnumConverter.AllowIntegerValues",
                        "A StringEnumConverter with AllowIntegerValues = false rejects integer strings and unnamed enum values on the target.");
                }
            }
        }
    }

    private static IEnumerable<RawNewtonsoftFinding> RecursivePayloads(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case)
    {
        var reported = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var site in Sites(context, SiteDirection.Deserialize).Where(s => Shape(context, s) is not { SetsMaxDepth: true }))
        {
            if (PayloadClosure.Of(site.Payloads).Recursive() is { } recursive && reported.Add(recursive))
            {
                yield return Finding(rule, @case, site.Call.Syntax.GetLocation(), AuditEngine.Name(recursive),
                    $"{AuditEngine.Name(recursive)} contains itself, and site {site.Site.Id} reads it without MaxDepth: the target stops at 64 levels.",
                    ("site", site.Site.Id), ("type", AuditEngine.Name(recursive)));
            }
        }
    }

    private static IEnumerable<RawNewtonsoftFinding> ToObjectDates(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case)
    {
        foreach (var site in context.Sites.Where(s => s.Site.Api.StartsWith("M:Newtonsoft.Json.Linq.JToken.ToObject", StringComparison.Ordinal)
            && s.Call.TargetMethod.Parameters.Any(p => p.Type.ToDisplayString() == "Newtonsoft.Json.JsonSerializer")))
        {
            if (PayloadClosure.Of(site.Payloads).Members.FirstOrDefault(m => IsDateTime(m.Type)) is { } date)
            {
                yield return Finding(rule, @case, site.Call.Syntax.GetLocation(), AuditEngine.Name(date.Member),
                    $"Site {site.Site.Id} converts a token with a serializer to a type with DateTime member {AuditEngine.Name(date.Member)}; the target applies the serializer's DateTimeZoneHandling.",
                    ("site", site.Site.Id));
            }
        }
    }

    /// <summary>One finding per project and case, at the first site of the direction the case applies to.</summary>
    private static IEnumerable<RawNewtonsoftFinding> DataDependent(DetectorContext context, NewtonsoftRule rule, NewtonsoftCase @case)
    {
        var direction = @case.Applies switch
        {
            "dom" => SiteDirection.Dom,
            "deserialize" => SiteDirection.Deserialize,
            var other => throw new InvalidDataException($"{rule.Id}/{@case.Name} applies to unknown '{other}'."),
        };
        if (context.Sites.FirstOrDefault(s => s.Site.Direction == direction) is { } site)
        {
            yield return Finding(rule, @case, site.Call.Syntax.GetLocation(), @case.Name,
                $"{@case.Note.Split(';')[0].Trim()}: the release in use ({@case.LabelFor(context.Current)}) and the target ({@case.LabelFor(context.Target)}) differ for some input; the project has {direction.ToString().ToLowerInvariant()} sites, the first is {site.Site.Id}.",
                ("site", site.Site.Id));
        }
    }

    private static IEnumerable<FoundSite> Sites(DetectorContext context, SiteDirection? direction) =>
        context.Sites.Where(s => s.Site.Direction != SiteDirection.Dom && (direction is null || s.Site.Direction == direction));

    /// <summary>The settings a site uses: its own, or the default settings when it passes none.</summary>
    private static SettingsShape? Shape(DetectorContext context, FoundSite site) =>
        site.Site.Settings.Kind == SettingsKind.None ? context.DefaultSettings : site.Shape;

    private static bool IsSerializableWithoutAttribute(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsSerializable: false, TypeKind: TypeKind.Class or TypeKind.Struct } named
        && named.AllInterfaces.Any(i => i.ToDisplayString() == "System.Runtime.Serialization.ISerializable")
        && !IsException(named);

    private static bool IsExceptionWithoutAttribute(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsSerializable: false } named && IsException(named);

    private static bool IsException(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Exception")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDataContractCollection(ITypeSymbol type) =>
        type is INamedTypeSymbol named && PayloadClosure.Attribute(named, "System.Runtime.Serialization.DataContractAttribute") is not null
        && named.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable);

    private static bool EnumMemberKeys(ITypeSymbol type) =>
        PayloadClosure.Dictionary(type) is { } dictionary
        && dictionary.TypeArguments[0] is INamedTypeSymbol { TypeKind: TypeKind.Enum } key
        && key.GetMembers().OfType<IFieldSymbol>().Any(f => PayloadClosure.Attribute(f, "System.Runtime.Serialization.EnumMemberAttribute") is not null);

    private static bool IsDecimal(ITypeSymbol type) =>
        Unwrapped(type).SpecialType == SpecialType.System_Decimal;

    private static bool IsDateTime(ITypeSymbol type) =>
        Unwrapped(type).SpecialType == SpecialType.System_DateTime;

    private static bool IsImmutableInterface(ITypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString() is "System.Collections.Immutable.IImmutableSet<T>" or "System.Collections.Immutable.IImmutableDictionary<TKey, TValue>";

    private static ITypeSymbol Unwrapped(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    private static RawNewtonsoftFinding Finding(NewtonsoftRule rule, NewtonsoftCase? @case, Location location, string symbol, string message, params (string Key, string? Value)[] details) =>
        new(rule, @case, location, symbol, message,
            new SortedDictionary<string, string>(details.Where(d => d.Value is not null).ToDictionary(d => d.Key, d => d.Value!), StringComparer.Ordinal));
}
