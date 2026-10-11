using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Newtonsoft;
using Offramp.Core.Model;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Newtonsoft;

/// <summary>A planned probe with its code: the body handed to <c>OfframpProbe.Check</c>, and the probe whose output it reads.</summary>
internal sealed record ProbeCode(PlannedProbe Probe, string Method, string Body, string? Reads);

/// <summary>Real payloads from <c>--samples</c>, by the payload type they are named after.</summary>
internal sealed record SampleFile(string Name, string Source, string TypeKey, string Label);

/// <summary>
/// The probes of one production project's sites, for one test project
/// (docs/spec/commands/newtonsoft.md#newtonsoft-tests; ADR 0071). Serialize sites first, so reading
/// sites can take their input from what a serialize probe of the same payload type and settings
/// writes.
/// </summary>
internal sealed class ProbePlanner
{
    private readonly ScannedProject _production;
    private readonly ProjectInfo _test;
    private readonly WorkspaceModel _model;
    private readonly ICompilationSource _compilations;
    private readonly IReadOnlyList<SampleFile> _samples;
    private readonly IReadOnlyList<string> _labels;
    private readonly string _serializeFile;
    private readonly string _deserializeFile;
    private readonly bool _typeProbes;
    private readonly SampleGenerator _generator;

    public ProbePlanner(ScannedProject production, ProjectInfo test, Compilation? testCompilation, WorkspaceModel model, ICompilationSource compilations,
        IReadOnlyList<SampleFile> samples, int samplesPerType, int objectDepth, string serializeFile, string deserializeFile)
    {
        _production = production;
        _test = test;
        _model = model;
        _compilations = compilations;
        _samples = samples;
        _labels = [.. SampleGenerator.Labels.Take(Math.Max(1, samplesPerType))];
        _serializeFile = serializeFile;
        _deserializeFile = deserializeFile;
        _typeProbes = testCompilation is not null && SameNewtonsoft(production.Compilation, testCompilation);
        _generator = new SampleGenerator(production.Compilation, Accessible, objectDepth);
    }

    public bool TypeProbesAllowed => _typeProbes;

    public List<ProbeCode> Serialize { get; } = [];

    public List<ProbeCode> Deserialize { get; } = [];

    public List<UncoveredSite> Uncovered { get; } = [];

    /// <summary>The sample files the planned probes read.</summary>
    public HashSet<SampleFile> UsedSamples { get; } = [];

    public void Plan()
    {
        var sites = _production.Sites.OrderBy(s => s.Site.Id, StringComparer.Ordinal).ToList();
        foreach (var site in sites.Where(s => s.Site.Direction == SiteDirection.Serialize))
        {
            PlanSerialize(site);
        }

        foreach (var site in sites.Where(s => s.Site.Direction != SiteDirection.Serialize))
        {
            PlanRead(site, sites);
        }
    }

    private void PlanSerialize(FoundSite site)
    {
        if (site.Site.EntryPoint is { } entry && Entry(entry) is { } member)
        {
            foreach (var label in _labels)
            {
                if (Call(member, label, null, null) is { } body)
                {
                    Add(site, ProbeKind.Entry, $"entry.{label}", label, ProbeInput.Generated, null, body, null);
                }
            }

            return;
        }

        var payloads = site.Payloads.Where(p => _generator.Expression(p, "typical") is not null).ToList();
        if (TypeProbeBlocked(site, payloads.Count > 0 ? null : "its payload type cannot be written as C#") is { } blocked)
        {
            Uncover(site, blocked.Reason, blocked.Detail);
            return;
        }

        for (var k = 0; k < payloads.Count; k++)
        {
            var kind = payloads.Count == 1 ? "type" : $"type{k + 1}";
            foreach (var label in _labels)
            {
                Add(site, ProbeKind.Type, $"{kind}.{label}", label, ProbeInput.Generated, null, TypeWrite(site, payloads[k], _generator.Expression(payloads[k], label)!), null);
            }
        }
    }

    private void PlanRead(FoundSite site, List<FoundSite> sites)
    {
        var inputs = Inputs(site, sites);
        if (inputs.Count == 0)
        {
            Uncover(site, "noInput", site.Site.Direction == SiteDirection.Dom
                ? "a DOM site reads JSON of no known type; there is no payload to give it"
                : $"no sample payload for {string.Join(", ", site.Site.PayloadTypes)} and nothing writes it with the same settings");
            return;
        }

        if (site.Site.EntryPoint is { Input: { } parameter } entry && Entry(entry) is IMethodSymbol method)
        {
            foreach (var input in inputs)
            {
                if (Call(method, "typical", parameter, Read(input)) is { } body)
                {
                    Add(site, ProbeKind.Entry, $"entry.{input.Label}", input.Label, input.Kind, input.From, body, input.Probe);
                }
            }

            return;
        }

        var payload = site.Payloads.FirstOrDefault(p => Accessible(p));
        if (TypeProbeBlocked(site, payload is null ? "its payload type is not visible to the test project" : null) is { } blocked)
        {
            Uncover(site, blocked.Reason, blocked.Detail);
            return;
        }

        foreach (var input in inputs)
        {
            Add(site, ProbeKind.Type, $"type.{input.Label}", input.Label, input.Kind, input.From, TypeRead(site, payload!, Read(input)), input.Probe);
        }
    }

    /// <summary>
    /// What a reading site is given: real payloads named after its payload type; else what a
    /// serialize probe of the same payload type and settings writes; else, when the test project can
    /// call Newtonsoft.Json, what the site's own settings write for a generated sample.
    /// </summary>
    private List<(string Label, ProbeInput Kind, string From, string? Probe)> Inputs(FoundSite site, List<FoundSite> sites)
    {
        var inputs = new List<(string, ProbeInput, string, string?)>();
        foreach (var sample in _samples.Where(s => site.Payloads.Any(p => TypeKey(p) == s.TypeKey)).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            UsedSamples.Add(sample);
            inputs.Add((sample.Label, ProbeInput.Sample, "Inputs/" + sample.Name, null));
        }

        // Real payloads and what the code itself writes: both, when there are both.
        if (!DirectRead(site) || site.Payloads.Count == 0)
        {
            return inputs;
        }

        var key = SettingsKey(site);
        var writer = key is null ? null : sites
            .Where(s => s.Site.Direction == SiteDirection.Serialize && SettingsKey(s) == key && s.Site.PayloadTypes.Intersect(site.Site.PayloadTypes, StringComparer.Ordinal).Any())
            .Select(s => Serialize.Where(p => p.Probe.Site == s.Site.Id && WritesPayload(p, s)).ToList())
            .FirstOrDefault(p => p.Count > 0);
        if (writer is not null)
        {
            return [.. inputs, .. writer.Select(p => ("roundtrip-" + p.Probe.Sample, ProbeInput.Roundtrip, "probe:" + p.Probe.Id, (string?)p.Probe.Id))];
        }

        var payload = site.Payloads[0];
        if (TypeProbeBlocked(site, _generator.Expression(payload, "typical") is null ? "its payload type cannot be written as C#" : null) is not null)
        {
            return inputs;
        }

        // Nothing in the project writes the payload this way: write it with the site's own settings.
        foreach (var label in _labels)
        {
            var id = Add(site, ProbeKind.Type, $"input.{label}", label, ProbeInput.Generated, null, TypeWrite(site, payload, _generator.Expression(payload, label)!), null, serializeClass: true);
            inputs.Add(("roundtrip-" + label, ProbeInput.Roundtrip, "probe:" + id, id));
        }

        return inputs;
    }

    /// <summary>A serialize probe whose observation is exactly the JSON of the payload: a type probe, or an entry probe at the site's own member returning a string.</summary>
    private bool WritesPayload(ProbeCode probe, FoundSite site) =>
        probe.Probe.Kind == ProbeKind.Type
        || site.Site.EntryPoint is { Depth: 0 } entry && Entry(entry) is IMethodSymbol { ReturnType.SpecialType: SpecialType.System_String } method
            && method.Parameters.Count(p => site.Payloads.Any(t => SymbolEqualityComparer.Default.Equals(t, p.Type))) == 1;

    /// <summary>Why a type probe cannot be written for the site, or null when it can.</summary>
    private (string Reason, string Detail)? TypeProbeBlocked(FoundSite site, string? payloadProblem)
    {
        if (!_typeProbes)
        {
            return site.Site.Uncovered is { } uncovered
                ? (Wire(uncovered.Reason), uncovered.Detail)
                : ("newtonsoftNotBound", $"{_test.Id} does not compile against {_production.Project.Id}'s Newtonsoft.Json, and the site has no entry point");
        }

        if (payloadProblem is not null)
        {
            return ("payloadNotGeneratable", payloadProblem);
        }

        return Settings(site) is null ? ("settingsNotReproducible", "the site's settings cannot be recreated by a test") : null;
    }

    /// <summary>The settings expression a type probe passes (empty for none), or null when it cannot be written.</summary>
    private string? Settings(FoundSite site) => site.Site.Settings.Kind switch
    {
        SettingsKind.None => "",
        SettingsKind.Inline when site.Settings is { } creation => OperationText.Of(creation),
        SettingsKind.Member when site.Settings is IMemberReferenceOperation { Member: var member } && Accessible(member) => OperationText.Of(site.Settings),
        _ => null,
    };

    private string TypeWrite(FoundSite site, ITypeSymbol payload, string sample)
    {
        var settings = Settings(site)!;
        if (site.Site.Api.Contains(".FromObject", StringComparison.Ordinal))
        {
            return $"() => global::Newtonsoft.Json.Linq.JToken.FromObject({sample}{Serializer(site, settings)}).ToString(global::Newtonsoft.Json.Formatting.None)";
        }

        if (IsSerializer(site))
        {
            return $"() => {{ var writer = new global::System.IO.StringWriter(global::System.Globalization.CultureInfo.InvariantCulture); ({settings}).Serialize(writer, {sample}); return writer.ToString(); }}";
        }

        return $"() => global::Newtonsoft.Json.JsonConvert.SerializeObject({sample}{(settings.Length == 0 ? "" : ", " + settings)})";
    }

    private string TypeRead(FoundSite site, ITypeSymbol payload, string input)
    {
        var settings = Settings(site)!;
        var type = CSharp6.Type(payload);
        if (site.Site.Api.Contains(".ToObject", StringComparison.Ordinal))
        {
            return $"() => global::Newtonsoft.Json.Linq.JToken.Parse({input}).ToObject<{type}>({Serializer(site, settings).TrimStart(',', ' ')})";
        }

        if (IsSerializer(site))
        {
            return $"() => ({settings}).Deserialize<{type}>(new global::Newtonsoft.Json.JsonTextReader(new global::System.IO.StringReader({input})))";
        }

        return $"() => global::Newtonsoft.Json.JsonConvert.DeserializeObject<{type}>({input}{(settings.Length == 0 ? "" : ", " + settings)})";
    }

    /// <summary>A JsonSerializer argument from the settings: the serializer itself, or one created from settings.</summary>
    private static string Serializer(FoundSite site, string settings) =>
        settings.Length == 0 ? "" : IsSerializer(site) ? ", " + settings : $", global::Newtonsoft.Json.JsonSerializer.Create({settings})";

    private static bool IsSerializer(FoundSite site) => site.Settings?.Type?.ToDisplayString() == "Newtonsoft.Json.JsonSerializer";

    /// <summary>
    /// A call of the entry point: on a new instance (its cheapest constructor) or the type, the JSON
    /// in <paramref name="input"/>, generated values elsewhere, and output parameters read back.
    /// </summary>
    private string? Call(ISymbol member, string label, string? input, string? json)
    {
        var receiver = member.IsStatic
            ? CSharp6.Type(member.ContainingType)
            : Generatability.Constructor(member.ContainingType, Accessible) is { } constructor && Arguments(constructor.Parameters, label) is { } ctorArguments
                ? $"new {CSharp6.Type(member.ContainingType)}({ctorArguments})"
                : member.ContainingType.IsValueType ? $"default({CSharp6.Type(member.ContainingType)})" : null;
        if (receiver is null)
        {
            return null;
        }

        if (member is IPropertySymbol property)
        {
            return $"() => {receiver}.{property.Name}";
        }

        var method = (IMethodSymbol)member;
        var locals = new StringBuilder();
        var arguments = new List<string>();
        var outputs = new List<string>();
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Name == input)
            {
                arguments.Add(JsonArgument(parameter.Type, json!));
            }
            else if (parameter.RefKind is RefKind.Out or RefKind.Ref)
            {
                var name = "output" + (outputs.Count + 1);
                var initial = parameter.RefKind == RefKind.Ref ? _generator.Expression(parameter.Type, label) ?? $"default({CSharp6.Type(parameter.Type)})" : $"default({CSharp6.Type(parameter.Type)})";
                locals.Append(System.Globalization.CultureInfo.InvariantCulture, $"var {name} = {initial}; ");
                arguments.Add((parameter.RefKind == RefKind.Out ? "out " : "ref ") + name);
                outputs.Add(name);
            }
            else if (Generatability.IsOutput(parameter))
            {
                var name = "output" + (outputs.Count + 1);
                var (create, read) = parameter.Type.ToDisplayString() switch
                {
                    "System.IO.Stream" or "System.IO.MemoryStream" => ("new global::System.IO.MemoryStream()", $"global::System.Text.Encoding.UTF8.GetString({name}.ToArray())"),
                    "System.Text.StringBuilder" => ("new global::System.Text.StringBuilder()", $"{name}.ToString()"),
                    _ => ("new global::System.IO.StringWriter(global::System.Globalization.CultureInfo.InvariantCulture)", $"{name}.ToString()"),
                };
                locals.Append(System.Globalization.CultureInfo.InvariantCulture, $"var {name} = {create}; ");
                arguments.Add(name);
                outputs.Add(read);
            }
            else if (_generator.Expression(parameter.Type, label) is { } value)
            {
                arguments.Add(value);
            }
            else
            {
                return null;
            }
        }

        var call = $"{receiver}.{method.Name}({string.Join(", ", arguments)})";
        if (outputs.Count == 0)
        {
            return method.ReturnsVoid ? null : $"() => {call}";
        }

        var observed = method.ReturnsVoid ? outputs : ["result", .. outputs];
        return method.ReturnsVoid
            ? $"() => {{ {locals}{call}; return new object[] {{ {string.Join(", ", observed)} }}; }}"
            : $"() => {{ {locals}var result = {call}; return new object[] {{ {string.Join(", ", observed)} }}; }}";
    }

    private string? Arguments(IEnumerable<IParameterSymbol> parameters, string label)
    {
        var values = parameters.Select(p => _generator.Expression(p.Type, label)).ToList();
        return values.Any(v => v is null) ? null : string.Join(", ", values);
    }

    private static string JsonArgument(ITypeSymbol type, string json) => type.ToDisplayString() switch
    {
        "System.IO.TextReader" or "System.IO.StringReader" => $"new global::System.IO.StringReader({json})",
        "System.IO.Stream" or "System.IO.MemoryStream" => $"new global::System.IO.MemoryStream(global::System.Text.Encoding.UTF8.GetBytes({json}))",
        _ => json,
    };

    private static string Read((string Label, ProbeInput Kind, string From, string? Probe) input) =>
        $"OfframpProbe.Input({CSharp6.String(input.From)})";

    private string Add(FoundSite site, ProbeKind kind, string suffix, string sample, ProbeInput input, string? from, string body, string? reads, bool serializeClass = false)
    {
        var id = ProbeId(site.Site.Id + "." + suffix);
        var file = serializeClass || site.Site.Direction == SiteDirection.Serialize ? _serializeFile : _deserializeFile;
        var code = new ProbeCode(new PlannedProbe
        {
            Id = id,
            Kind = kind,
            Direction = site.Site.Direction,
            Site = site.Site.Id,
            TestProject = _test.Id,
            Sample = sample,
            Input = input,
            InputFrom = from,
            File = file,
        }, "Probe_" + CSharp6.Identifier(id), body, reads);
        (file == _serializeFile ? Serialize : Deserialize).Add(code);
        return id;
    }

    private void Uncover(FoundSite site, string reason, string detail) =>
        Uncovered.Add(new UncoveredSite(site.Site.Id, _production.Project.Id, reason, detail));

    private ISymbol? Entry(SiteEntryPoint entry)
    {
        var project = _model.Projects.FirstOrDefault(p => p.Id == entry.Project);
        var compilation = project is null ? null
            : project.Id == _production.Project.Id ? _production.Compilation
            : CompilationLoader.PreferredTarget(project) is { } tfm ? _compilations.LoadForProject(project, tfm) : null;
        return compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(entry.Member, compilation);
    }

    /// <summary>Public all the way out, or internal to a production project that names the test project a friend.</summary>
    private bool Accessible(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current is ITypeSymbol { TypeKind: TypeKind.Array or TypeKind.TypeParameter })
            {
                continue;
            }

            if (current.DeclaredAccessibility != Accessibility.Public
                && !(current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal && Friend(current.ContainingAssembly)))
            {
                return false;
            }
        }

        return symbol is not INamedTypeSymbol { IsGenericType: true } generic || generic.TypeArguments.All(Accessible);
    }

    private bool Friend(IAssemblySymbol? assembly) =>
        assembly is not null && _model.Projects.FirstOrDefault(p => string.Equals(p.AssemblyName ?? p.Name, assembly.Name, StringComparison.OrdinalIgnoreCase)) is { } declaring
        && declaring.InternalsVisibleTo.Contains(_test.AssemblyName ?? _test.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the test project compiles against the same Newtonsoft.Json assembly as the production project.</summary>
    internal static bool SameNewtonsoft(Compilation production, Compilation test)
    {
        static AssemblyIdentity? Identity(Compilation compilation) => compilation.References
            .Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()
            .FirstOrDefault(a => a.Name == NewtonsoftReferences.PackageId)?.Identity;
        return Identity(production) is { } expected && expected.Equals(Identity(test));
    }

    /// <summary>A deserialize call whose JSON is the payload's own text (not a token inside a DOM).</summary>
    private static bool DirectRead(FoundSite site) =>
        site.Site.Direction == SiteDirection.Deserialize && !site.Site.Api.Contains(".ToObject", StringComparison.Ordinal);

    /// <summary>What makes two sites' JSON interchangeable: the same settings member, the same inline settings, or none.</summary>
    private static string? SettingsKey(FoundSite site) => site.Site.Settings.Kind switch
    {
        SettingsKind.None => "none",
        SettingsKind.Member => "member:" + site.Site.Settings.Symbol,
        SettingsKind.Inline => $"inline:{site.Site.Settings.File}:{site.Site.Settings.Line}",
        _ => null,
    };

    /// <summary>The name a sample file uses for a payload type: its full metadata name, with <c>[]</c> for a collection of it.</summary>
    internal static string TypeKey(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return TypeKey(array.ElementType) + "[]";
        }

        if (type is INamedTypeSymbol named && PayloadClosure.Dictionary(named) is null && PayloadClosure.Elements(named).FirstOrDefault() is { } element
            && named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        {
            return TypeKey(element) + "[]";
        }

        return type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
            .WithGenericsOptions(SymbolDisplayGenericsOptions.None));
    }

    /// <summary>Letters, digits, and <c>. # - _</c>: a file name on every OS.</summary>
    internal static string ProbeId(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '#' or '-' or '_' ? c : '_');
        }

        return builder.ToString();
    }

    private static string Wire(UncoveredReason reason)
    {
        var name = reason.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
