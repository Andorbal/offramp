using System.Text.Json.Nodes;
using NuGet.Versioning;
using Offramp.Analysis.Rules;
using Offramp.Core.Diagnostics;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>A behavior change between Newtonsoft.Json releases (<c>rules/newtonsoft-breaking.yml</c>, ADR 0068).</summary>
public sealed record NewtonsoftRule
{
    public required string Id { get; init; }

    public required Severity Severity { get; init; }

    public required string Title { get; init; }

    /// <summary>The matcher that finds affected code (<c>data-dependent</c>: none, reported per project).</summary>
    public required string Detector { get; init; }

    public required string Recommendation { get; init; }

    public required IReadOnlyList<NewtonsoftCase> Cases { get; init; }
}

/// <summary>One behavior of a rule, as labeled version segments, with the example that proves them.</summary>
public sealed record NewtonsoftCase
{
    public required string Name { get; init; }

    /// <summary>The release-note line(s) the case comes from.</summary>
    public required string Note { get; init; }

    /// <summary>Ordered and contiguous: the first has no start.</summary>
    public required IReadOnlyList<VersionSegment> Segments { get; init; }

    /// <summary>C# with <c>public static class Example { public static string Run() }</c>.</summary>
    public required string Example { get; init; }

    /// <summary>For caught-exception cases: the APIs (documentation IDs, any overload) inside the <c>try</c>.</summary>
    public IReadOnlyList<string> Apis { get; init; } = [];

    /// <summary>For caught-exception cases: the exception the old versions throw.</summary>
    public string? Caught { get; init; }

    /// <summary>For caught-exception cases: the exception the target throws.</summary>
    public string? Thrown { get; init; }

    /// <summary>For data-dependent cases: the site direction that makes the case apply to a project.</summary>
    public string? Applies { get; init; }

    /// <summary>The label of the segment a version falls in.</summary>
    public string LabelFor(NuGetVersion version) =>
        Segments.Last(s => s.From is null || version >= s.From).Label;

    /// <summary>True when the behavior differs between the version in use and the target.</summary>
    public bool Differs(NuGetVersion current, NuGetVersion target) =>
        !string.Equals(LabelFor(current), LabelFor(target), StringComparison.Ordinal);
}

public sealed record VersionSegment(NuGetVersion? From, string Label);

/// <summary>The rule pack, loaded once from the embedded <c>rules/newtonsoft-breaking.yml</c>.</summary>
public static class NewtonsoftRules
{
    private static readonly Lazy<IReadOnlyList<NewtonsoftRule>> Loaded = new(() => Parse(RuleFiles.Load("newtonsoft-breaking.yml")));

    public static IReadOnlyList<NewtonsoftRule> All => Loaded.Value;

    internal static IReadOnlyList<NewtonsoftRule> Parse(JsonNode root)
    {
        var rules = new List<NewtonsoftRule>();
        foreach (var (id, node) in root["rules"]!.AsObject())
        {
            rules.Add(new NewtonsoftRule
            {
                Id = id,
                Severity = ParseSeverity(Text(node, "severity", id), id),
                Title = Text(node, "title", id),
                Detector = Text(node, "detector", id),
                Recommendation = Text(node, "recommendation", id),
                Cases = [.. node!["cases"]!.AsArray().Select(c => Case(c!, id))],
            });
        }

        return [.. rules.OrderBy(r => r.Id, StringComparer.Ordinal)];
    }

    private static NewtonsoftCase Case(JsonNode node, string id)
    {
        var name = Text(node, "name", id);
        var segments = node["segments"]!.AsArray()
            .Select(s => new VersionSegment(s!["from"] is { } from ? NuGetVersion.Parse(from.ToString()) : null, Text(s, "label", id)))
            .ToList();
        if (segments.Count < 2 || segments[0].From is not null || segments.Skip(1).Any(s => s.From is null)
            || segments.Skip(1).Zip(segments.Skip(2)).Any(p => p.First.From! >= p.Second.From!))
        {
            throw new InvalidDataException($"{id}/{name}: segments must start without 'from' and then ascend.");
        }

        return new NewtonsoftCase
        {
            Name = name,
            Note = Text(node, "note", id),
            Segments = segments,
            Example = Text(node, "example", id),
            Apis = node["apis"] is JsonArray apis ? [.. apis.Select(a => a!.GetValue<string>())] : [],
            Caught = node["caught"]?.GetValue<string>(),
            Thrown = node["thrown"]?.GetValue<string>(),
            Applies = node["applies"]?.GetValue<string>(),
        };
    }

    private static Severity ParseSeverity(string value, string id) => value switch
    {
        "error" => Severity.Error,
        "warning" => Severity.Warning,
        "info" => Severity.Info,
        _ => throw new InvalidDataException($"Newtonsoft rule {id} has an unknown severity '{value}'."),
    };

    private static string Text(JsonNode? node, string key, string id) =>
        node?[key]?.GetValue<string>() ?? throw new InvalidDataException($"Newtonsoft rule {id} lacks '{key}'.");
}
