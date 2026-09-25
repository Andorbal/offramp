using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Init;

/// <summary>
/// Detects defaults for <c>offramp.yml</c>, renders it with comments, and writes
/// it together with the <c>.gitignore</c> entries for Offramp's state directory.
/// See <c>docs/spec/03-configuration.md#init</c> and <c>docs/decisions/0004-init-writes.md</c>.
/// </summary>
public static partial class InitPlanner
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx", ".slnf"];

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "artifacts", "TestResults",
    };

    public static InitDetection Detect(string repositoryRoot, OfframpConfig config, DiagnosticBag diagnostics)
    {
        var candidates = FindSolutions(repositoryRoot);
        var solution = config.Solution ?? ChooseSolution(candidates);
        if (solution is null && candidates.Count(IsSolutionFile) > 1)
        {
            diagnostics.Report(DiagnosticCatalog.OFR0020,
                $"Found {candidates.Count} solutions and none is at the repository root alone; `solution:` is left empty. Pass --solution to choose.",
                severity: Severity.Warning,
                data: [KeyValuePair.Create<string, JsonNode?>("candidates", new JsonArray([.. candidates.Select(c => (JsonNode?)c)]))]);
        }

        var cpmFile = File.Exists(Path.Combine(repositoryRoot, "Directory.Packages.props"))
            ? "Directory.Packages.props"
            : config.Deps.Cpm.File;

        return new InitDetection
        {
            Values = new InitValues
            {
                Target = config.Target,
                Solution = solution,
                VerifyMode = config.Verify.Mode,
                CpmFile = cpmFile,
                Pins = config.Deps.Pins,
            },
            SolutionCandidates = candidates,
            ConfigExists = File.Exists(Path.Combine(repositoryRoot, ConfigLoader.DefaultFileName)),
        };
    }

    /// <summary>
    /// Writes (unless <paramref name="dryRun"/>) the configuration and the
    /// <c>.gitignore</c> entries. Never overwrites an existing file without <paramref name="force"/>.
    /// </summary>
    public static InitResult Apply(
        string repositoryRoot, InitDetection detection, InitValues values, string stateDirectory,
        bool dryRun, bool force, bool interactive, DiagnosticBag diagnostics)
    {
        var content = Render(values);
        var configPath = Path.Combine(repositoryRoot, ConfigLoader.DefaultFileName);
        var exists = File.Exists(configPath);
        var gitignore = PlanGitignore(repositoryRoot, stateDirectory);

        if (exists && !force)
        {
            diagnostics.Report(DiagnosticCatalog.OFR0030,
                $"{ConfigLoader.DefaultFileName} already exists and was left unchanged. Re-run with --force to replace it.",
                new DiagnosticLocation(File: ConfigLoader.DefaultFileName));
            return Result(false);
        }

        if (!dryRun)
        {
            File.WriteAllText(configPath, content, new UTF8Encoding(false));
            WriteGitignore(repositoryRoot, gitignore);
        }

        return Result(!dryRun);

        InitResult Result(bool written) => new()
        {
            ConfigFile = ConfigLoader.DefaultFileName,
            Written = written,
            DryRun = dryRun,
            Replaced = written && exists,
            Interactive = interactive,
            Values = values,
            SolutionCandidates = detection.SolutionCandidates,
            Gitignore = written || dryRun ? gitignore : gitignore with { Added = [] },
            Content = content,
        };
    }

    /// <summary>The <c>.gitignore</c> entries for Offramp's state; the ledger stays committed.</summary>
    public static IReadOnlyList<string> GitignoreEntries(string stateDirectory)
    {
        var state = RepoPaths.Normalize(stateDirectory).TrimEnd('/');
        return
        [
            $"{state}/cache/",
            $"{state}/journal/",
            $"{state}/verify/",
            $"{state}/*.binlog",
            $"{state}/*.complog",
        ];
    }

    public static IReadOnlyList<string> FindSolutions(string repositoryRoot)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(repositoryRoot);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(dir);
                subdirectories = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            found.AddRange(files
                .Where(f => SolutionExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => RepoPaths.ToRepositoryRelative(repositoryRoot, f)));
            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && !SkippedDirectories.Contains(name))
                {
                    pending.Push(sub);
                }
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The only <c>.sln</c>/<c>.slnx</c> in the repository; otherwise the only one at
    /// the root; otherwise null. Solution filters are never chosen automatically.
    /// </summary>
    public static string? ChooseSolution(IReadOnlyList<string> candidates)
    {
        var solutions = candidates.Where(IsSolutionFile).ToList();
        if (solutions.Count == 1)
        {
            return solutions[0];
        }

        var atRoot = solutions.Where(s => !s.Contains('/', StringComparison.Ordinal)).ToList();
        return atRoot.Count == 1 ? atRoot[0] : null;
    }

    private static bool IsSolutionFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static GitignoreChange PlanGitignore(string repositoryRoot, string stateDirectory)
    {
        var path = Path.Combine(repositoryRoot, ".gitignore");
        var existing = File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal)
            : [];
        var entries = GitignoreEntries(stateDirectory);
        return new GitignoreChange
        {
            File = ".gitignore",
            Added = [.. entries.Where(e => !existing.Contains(e))],
            Present = [.. entries.Where(existing.Contains)],
        };
    }

    private static void WriteGitignore(string repositoryRoot, GitignoreChange change)
    {
        if (change.Added.Count == 0)
        {
            return;
        }

        var path = Path.Combine(repositoryRoot, ".gitignore");
        var builder = new StringBuilder();
        if (File.Exists(path))
        {
            var current = File.ReadAllText(path);
            if (current.Length > 0 && !current.EndsWith('\n'))
            {
                builder.Append('\n');
            }

            if (current.Length > 0)
            {
                builder.Append('\n');
            }
        }

        builder.Append("# Offramp state (the ledger stays committed; see offramp.yml paths.state)\n");
        foreach (var entry in change.Added)
        {
            builder.Append(entry).Append('\n');
        }

        File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Renders <c>offramp.yml</c> with comments. Deterministic for the same values.</summary>
    public static string Render(InitValues values)
    {
        var b = new StringBuilder();
        b.Append("# offramp.yml: Offramp configuration.\n");
        b.Append("# Reference: https://github.com/Andorbal/offramp/blob/main/docs/spec/03-configuration.md\n");
        b.Append("# Precedence: built-in defaults < this file < OFFRAMP_* environment variables < command-line flags.\n");
        b.Append("version: 1\n\n");
        b.Append("# Integer major version of the modern target (10 = net10.0). --target overrides.\n");
        b.Append("target: ").Append(values.Target.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
        b.Append("# The solution (.sln, .slnx, or .slnf) Offramp works on.\n");
        b.Append("solution: ").Append(values.Solution is null ? "null" : Scalar(values.Solution)).Append("\n\n");
        b.Append("paths:\n");
        b.Append("  # Repository-relative globs. Excluded projects stay in the model but are never modified.\n");
        b.Append("  exclude: []\n");
        b.Append("  state: .offramp\n\n");
        b.Append("verify:\n");
        b.Append("  # build: dotnet build the affected projects | command: run verify.command | none\n");
        b.Append("  mode: ").Append(Scalar(values.VerifyMode)).Append('\n');
        b.Append("  timeoutSeconds: 1800\n");
        b.Append("  configuration: Debug\n");
        b.Append("  # Extra -p: values for every verification build and for scan, for example:\n");
        b.Append("  #   GenerateSerializationAssemblies: \"Off\"\n");
        b.Append("  properties: {}\n");
        b.Append("  noWarn: []\n");
        b.Append("  restore: true\n");
        b.Append("  # For movers: rollback | keep\n");
        b.Append("  onFailure: rollback\n\n");
        b.Append("deps:\n");
        b.Append("  # Packages that must share one version, for example:\n");
        b.Append("  #   - prefix: \"Microsoft.Extensions.\"\n");
        b.Append("  families: []\n");
        b.Append("  # Hard version pins. Always give a reason.\n");
        if (values.Pins.Count == 0)
        {
            b.Append("  #   - package: Newtonsoft.Json\n");
            b.Append("  #     project: src/Customer.Api/Customer.Api.csproj\n");
            b.Append("  #     version: \"9.0.1\"\n");
            b.Append("  #     reason: \"Customer integrations depend on 9.x serialization behavior\"\n");
            b.Append("  pins: []\n");
        }
        else
        {
            b.Append("  pins:\n");
            foreach (var pin in values.Pins)
            {
                b.Append("    - package: ").Append(Scalar(pin.Package)).Append('\n');
                if (pin.Project is not null)
                {
                    b.Append("      project: ").Append(Scalar(pin.Project)).Append('\n');
                }

                b.Append("      version: ").Append(Quote(pin.Version)).Append('\n');
                b.Append("      reason: ").Append(pin.Reason is null ? "null" : Quote(pin.Reason)).Append('\n');
            }
        }

        b.Append("  cpm:\n");
        b.Append("    # Where `deps consolidate` writes PackageVersion items.\n");
        b.Append("    file: ").Append(Scalar(values.CpmFile)).Append('\n');
        b.Append("    scope: solution\n\n");
        b.Append("move:\n");
        b.Append("  # none | per-project | batch:N | end\n");
        b.Append("  verify: end\n");
        b.Append("  namespaceMismatch: allow\n\n");
        b.Append("# Per-rule severity overrides. Always give a reason, for example:\n");
        b.Append("#   OFR3105: { severity: none, reason: \"We never run on Linux\" }\n");
        b.Append("rules: {}\n\n");
        b.Append("llm:\n");
        b.Append("  # LLM garnish (naming, ranking) stays off unless enabled here or with --llm.\n");
        b.Append("  enabled: false\n");
        return b.ToString();
    }

    /// <summary>A plain YAML scalar when unambiguous, otherwise a double-quoted one.</summary>
    internal static string Scalar(string value) =>
        PlainSafe().IsMatch(value) && !LooksTyped().IsMatch(value) ? value : Quote(value);

    internal static string Quote(string value)
    {
        var b = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        b.Append(c);
                    }

                    break;
            }
        }

        return b.Append('"').ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_./-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainSafe();

    [GeneratedRegex(@"^([-+]?[0-9.]+([eE][-+]?[0-9]+)?|true|false|null|yes|no|on|off|~)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LooksTyped();
}
