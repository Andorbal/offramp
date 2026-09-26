using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Seams;
using Offramp.Llm;

namespace Offramp.Cli.Infrastructure;

/// <summary>
/// <c>llm.uses: naming</c>: interface names for seams and <c>extract interface</c>. The model
/// sees only the type's name, its members' signatures, and the callers' type names; the answer
/// must be a C# interface name (<c>I</c> + PascalCase), else the rule's <c>I</c> + type name stays.
/// </summary>
public static partial class LlmNaming
{
    /// <summary>At most this many seams are named, in rank order; the rest keep the rule's name.</summary>
    public const int MaxSeams = 10;

    private const string System = "You name C# interfaces for a .NET migration tool. Answer with the name only, through the given JSON schema.";

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "A C# interface name: I followed by PascalCase." } },
        ["required"] = new JsonArray("name"),
        ["additionalProperties"] = false,
    };

    public static async Task<SeamsResult> NameSeamsAsync(LlmGate llm, SeamsResult result, CancellationToken cancellationToken)
    {
        var taken = new HashSet<string>(result.Seams.Select(s => s.ProposedInterface), StringComparer.Ordinal);
        var seams = new List<Seam>();
        foreach (var (seam, index) in result.Seams.Select((s, i) => (s, i)))
        {
            if (index >= MaxSeams)
            {
                seams.Add(seam);
                continue;
            }

            taken.Remove(seam.ProposedInterface);
            var name = await NameAsync(llm, seam.BoundaryType, seam.Members.Select(m => m.Signature), seam.Callers, taken, cancellationToken);
            seams.Add(name is null ? seam : seam with { ProposedInterface = name, Source = LlmGate.Source });
            taken.Add(name ?? seam.ProposedInterface);
        }

        return result with { Seams = seams };
    }

    /// <summary>An interface name for a type, or null when the model's answer is not usable (OFR9001).</summary>
    public static Task<string?> NameAsync(LlmGate llm, string type, IEnumerable<string> members, IEnumerable<string> callers, IReadOnlySet<string> taken, CancellationToken cancellationToken)
    {
        var shortName = type[(type.LastIndexOf('.') + 1)..];
        var prompt = $"""
            The class {shortName} ({type}) is used by {string.Join(", ", callers.Select(c => c[(c.LastIndexOf('.') + 1)..]).DefaultIfEmpty("other classes"))} through these members:
            {string.Join("\n", members.Select(m => "- " + m).DefaultIfEmpty("- (all its public members)"))}

            Callers will depend on an interface instead of the class, so the class can move behind a remote boundary.
            Name that interface for what it does for its callers, not for how it is implemented. Answer with the name only.
            """;
        return llm.AskAsync(new LlmRequest { Use = LlmGate.Naming, System = System, Prompt = prompt, Schema = Schema, MaxTokens = 64 },
            answer => answer["name"]?.GetValue<string>()?.Trim() is { } name && Valid(name) && !taken.Contains(name) ? name : null,
            $"the interface of {type}", cancellationToken);
    }

    /// <summary>I + an uppercase letter, then letters and digits; not a keyword.</summary>
    public static bool Valid(string name) =>
        InterfaceName().IsMatch(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    [GeneratedRegex("^I[A-Z][A-Za-z0-9]{1,62}$")]
    private static partial Regex InterfaceName();
}
