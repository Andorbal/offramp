using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Core.Diagnostics;

namespace Offramp.Core.Configuration;

/// <summary>
/// The typed view of <c>offramp.yml</c> merged with environment variables and
/// command-line flags. Property defaults here are the built-in defaults; see
/// <c>docs/spec/03-configuration.md</c> and <c>docs/decisions/0003-configuration-loading.md</c>.
/// </summary>
public sealed record OfframpConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Integer major version of the modern target (10 → <c>net10.0</c>).</summary>
    public int Target { get; init; } = 10;

    public string? Solution { get; init; }

    public PathsConfig Paths { get; init; } = new();

    public IReadOnlyList<ProjectOverride> Projects { get; init; } = [];

    public VerifyConfig Verify { get; init; } = new();

    public DepsConfig Deps { get; init; } = new();

    public MoveConfig Move { get; init; } = new();

    public RulesConfig Rules { get; init; } = new();

    public SeamsConfig Seams { get; init; } = new();

    public ServiceConfig Service { get; init; } = new();

    public LlmConfig Llm { get; init; } = new();

    public ReportConfig Report { get; init; } = new();

    public DeadCodeConfig DeadCode { get; init; } = new();

    /// <summary>The target framework moniker for <see cref="Target"/>.</summary>
    [JsonIgnore]
    public string TargetFramework => TargetMoniker(Target);

    public static string TargetMoniker(int major) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"net{major}.0");

    /// <summary>Severity overrides from <c>rules:</c>, keyed by code.</summary>
    public IReadOnlyDictionary<string, SeverityOverride> SeverityOverrides() => Rules.ToSeverityOverrides();
}

public sealed record PathsConfig
{
    /// <summary>Repository-relative globs; excluded projects stay in the model but are never modified.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Where workspace.json, cache, ledger, and journals live.</summary>
    public string State { get; init; } = ".offramp";
}

public sealed record ProjectOverride
{
    public string Path { get; init; } = "";

    public string? Kind { get; init; }

    public bool Frozen { get; init; }
}

public sealed record VerifyConfig
{
    /// <summary><c>build</c>, <c>command</c>, or <c>none</c>.</summary>
    public string Mode { get; init; } = "build";

    public string? Command { get; init; }

    public int TimeoutSeconds { get; init; } = 1800;

    public string Configuration { get; init; } = "Debug";

    public SortedDictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> NoWarn { get; init; } = [];

    public IReadOnlyList<string> WarnAsError { get; init; } = [];

    public VerifyProjectsConfig Projects { get; init; } = new();

    public bool Restore { get; init; } = true;

    /// <summary><c>rollback</c> or <c>keep</c>, for movers.</summary>
    public string OnFailure { get; init; } = "rollback";
}

public sealed record VerifyProjectsConfig
{
    public IReadOnlyList<string> Include { get; init; } = [];

    public IReadOnlyList<string> Exclude { get; init; } = [];
}

public sealed record DepsConfig
{
    /// <summary>Null means use <c>nuget.config</c>.</summary>
    public IReadOnlyList<string>? Feeds { get; init; }

    public bool IncludePrerelease { get; init; }

    public bool PreferNewest { get; init; }

    public IReadOnlyList<PackageFamily> Families { get; init; } = [];

    public IReadOnlyList<PackagePin> Pins { get; init; } = [];

    public IReadOnlyList<string> Ignore { get; init; } = [];

    public CpmConfig Cpm { get; init; } = new();

    public RedirectsConfig Redirects { get; init; } = new();
}

public sealed record PackageFamily
{
    public string Prefix { get; init; } = "";

    /// <summary>The family this prefix joins, when it is not its own.</summary>
    public string? Family { get; init; }
}

public sealed record PackagePin
{
    public string Package { get; init; } = "";

    /// <summary>Null pins the package everywhere.</summary>
    public string? Project { get; init; }

    public string Version { get; init; } = "";

    public string? Reason { get; init; }
}

public sealed record CpmConfig
{
    public string File { get; init; } = "Directory.Packages.props";

    /// <summary><c>solution</c> or <c>repo</c>.</summary>
    public string Scope { get; init; } = "solution";
}

public sealed record RedirectsConfig
{
    public IReadOnlyList<string> Manage { get; init; } = [];
}

public sealed record MoveConfig
{
    /// <summary><c>none</c>, <c>per-project</c>, <c>batch:N</c>, or <c>end</c>.</summary>
    public string Verify { get; init; } = "end";

    /// <summary><c>allow</c>, <c>warn</c>, or <c>block</c>.</summary>
    public string NamespaceMismatch { get; init; } = "allow";

    /// <summary><c>closure</c> or <c>none</c>.</summary>
    public string CoMove { get; init; } = "closure";

    public IReadOnlyList<string> SuppressAnalyzers { get; init; } = ["IDE0130"];

    public MoveTestsConfig Tests { get; init; } = new();
}

public sealed record MoveTestsConfig
{
    public IReadOnlyList<string> Frameworks { get; init; } = ["xunit", "nunit", "mstest", "tunit"];

    /// <summary><c>high</c>, <c>medium</c>, or <c>low</c>.</summary>
    public string HelperMinConfidence { get; init; } = "high";

    public bool StripTestsSegment { get; init; } = true;

    public string TargetSuffix { get; init; } = ".Tests";
}

/// <summary>
/// <c>rules:</c> mixes per-code overrides (<c>OFR3105: { severity: none, reason: ... }</c>)
/// with the <c>packs:</c> key. Codes land in <see cref="Overrides"/>.
/// </summary>
public sealed record RulesConfig
{
    public RulePacksConfig Packs { get; init; } = new();

    /// <summary>Per-code overrides. Settable because System.Text.Json cannot bind extension data through init.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Overrides { get; set; }

    public IReadOnlyDictionary<string, SeverityOverride> ToSeverityOverrides()
    {
        var result = new SortedDictionary<string, SeverityOverride>(StringComparer.Ordinal);
        if (Overrides is null)
        {
            return result;
        }

        foreach (var (code, value) in Overrides)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? reason = value.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            if (!value.TryGetProperty("severity", out var s) || s.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            Severity? severity = s.GetString() switch
            {
                "none" => null,
                "info" => Severity.Info,
                "warning" => Severity.Warning,
                "error" => Severity.Error,
                _ => Severity.Error,
            };
            result[code] = new SeverityOverride(severity, reason);
        }

        return result;
    }
}

public sealed record RulePacksConfig
{
    /// <summary>Rule packs to disable: core, web, desktop, data, serialization, native.</summary>
    public IReadOnlyList<string> Disable { get; init; } = [];
}

public sealed record SeamsConfig
{
    public IReadOnlyList<string> UnportableSources { get; init; } = ["audit"];

    public IReadOnlyList<string> UnportableSymbols { get; init; } = [];

    /// <summary><c>auto</c>, <c>net10-windows</c>, or <c>net48</c>.</summary>
    public string HostFramework { get; init; } = "auto";
}

public sealed record ServiceConfig
{
    /// <summary><c>linux</c>, <c>windows</c>, or <c>both</c>.</summary>
    public string Host { get; init; } = "linux";

    public bool Dockerfile { get; init; } = true;

    public bool K8s { get; init; }

    public bool HealthEndpoint { get; init; } = true;

    /// <summary><c>json-console</c> or <c>simple</c>.</summary>
    public string Logging { get; init; } = "json-console";
}

public sealed record LlmConfig
{
    public bool Enabled { get; init; }

    /// <summary><c>openai</c> (any OpenAI-compatible URL) or <c>anthropic</c>.</summary>
    public string Provider { get; init; } = "openai";

    public string Url { get; init; } = "http://localhost:1234/v1";

    public string? Model { get; init; }

    /// <summary>Name of the environment variable holding the API key. The key itself is never stored.</summary>
    public string ApiKeyEnv { get; init; } = "OFFRAMP_LLM_API_KEY";

    public IReadOnlyList<string> Uses { get; init; } = ["naming", "ranking"];
}

public sealed record ReportConfig
{
    /// <summary>Null derives the title from the repository directory name.</summary>
    public string? Title { get; init; }

    public string Ledger { get; init; } = ".offramp/ledger";
}

public sealed record DeadCodeConfig
{
    /// <summary>Assembly names other repositories consume; their public symbols are never above <c>medium</c>.</summary>
    public IReadOnlyList<string> ExternalConsumers { get; init; } = [];
}
