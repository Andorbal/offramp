using System.CommandLine;
using System.Globalization;
using Offramp.Core.Diagnostics;

namespace Offramp.Cli.Infrastructure;

/// <summary>Values of the global options, bound once per invocation.</summary>
public sealed record GlobalSettings
{
    public int? Target { get; init; }

    public string? Solution { get; init; }

    public string? Workspace { get; init; }

    public string? Config { get; init; }

    public bool Json { get; init; }

    public string? Out { get; init; }

    public bool DryRun { get; init; }

    public bool Apply { get; init; }

    public bool Yes { get; init; }

    public bool Quiet { get; init; }

    public bool Verbose { get; init; }

    public bool NoCache { get; init; }

    /// <summary>True for <c>--llm</c>, false for <c>--no-llm</c>, null when neither was given.</summary>
    public bool? Llm { get; init; }

    public FailOn FailOn { get; init; } = FailOn.Error;

    /// <summary>Treat a stale workspace model (OFR0002) as an error.</summary>
    public bool FailOnStale { get; init; }
}

/// <summary>
/// The global options of <c>docs/spec/01-cli-conventions.md</c>, recursive on the
/// root command so every command accepts them.
/// </summary>
public sealed class GlobalOptions
{
    public Option<int?> Target { get; } = new("--target", "-t")
    {
        Description = "Integer major version of the modern target (10 = net10.0). Default: config target (10).",
        HelpName = "N",
        Recursive = true,
    };

    public Option<string?> Solution { get; } = new("--solution", "-s")
    {
        Description = "The .sln/.slnx/.slnf to work on; required when more than one exists.",
        HelpName = "PATH",
        Recursive = true,
    };

    public Option<string?> Workspace { get; } = new("--workspace")
    {
        Description = "Workspace model to read. Default: .offramp/workspace.json.",
        HelpName = "PATH",
        Recursive = true,
    };

    public Option<string?> Config { get; } = new("--config")
    {
        Description = "Configuration file. Default: offramp.yml at the repository root.",
        HelpName = "PATH",
        Recursive = true,
    };

    public Option<bool> Json { get; } = new("--json")
    {
        Description = "JSON envelope on stdout, NDJSON progress on stderr, no colors.",
        Recursive = true,
    };

    public Option<string?> Out { get; } = new("--out", "-o")
    {
        Description = "Write the primary output (JSON, HTML, DOT, plan files) to a file.",
        HelpName = "PATH",
        Recursive = true,
    };

    public Option<bool> DryRun { get; } = new("--dry-run")
    {
        Description = "Show what would change without changing anything (the default for commands that modify the repository).",
        Recursive = true,
    };

    public Option<bool> Apply { get; } = new("--apply")
    {
        Description = "Perform the changes.",
        Recursive = true,
    };

    public Option<bool> Yes { get; } = new("--yes", "-y")
    {
        Description = "Skip interactive confirmations (implied when not a terminal).",
        Recursive = true,
    };

    public Option<bool> Quiet { get; } = new("--quiet", "-q")
    {
        Description = "No progress; only the result and errors.",
        Recursive = true,
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Debug detail on stderr.",
        Recursive = true,
    };

    public Option<bool> NoCache { get; } = new("--no-cache")
    {
        Description = "Ignore .offramp/cache/.",
        Recursive = true,
    };

    public Option<bool> NoLlm { get; } = new("--no-llm")
    {
        Description = "Never call an LLM (the default unless llm.enabled is set).",
        Recursive = true,
    };

    public Option<bool> Llm { get; } = new("--llm")
    {
        Description = "Allow LLM garnish (naming, ranking) for this run.",
        Recursive = true,
    };

    public Option<string> FailOn { get; } = new("--fail-on")
    {
        Description = "Exit 1 when any diagnostic is at or above this level.",
        HelpName = "info|warning|error|never",
        DefaultValueFactory = _ => "error",
        Recursive = true,
    };

    public Option<bool> FailOnStale { get; } = new("--fail-on-stale")
    {
        Description = "Treat a stale workspace model (OFR0002) as an error.",
        Recursive = true,
    };

    public GlobalOptions()
    {
        Target.Validators.Add(result =>
        {
            // Conversion errors ("ten") are reported by the parser; only range-check integers here.
            if (result.Tokens.Count > 0
                && int.TryParse(result.Tokens[0].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                && value < 5)
            {
                result.AddError(string.Create(CultureInfo.InvariantCulture,
                    $"--target must be 5 or higher (a modern .NET major version); got {value}."));
            }
        });
        FailOn.AcceptOnlyFromAmong("info", "warning", "error", "never");
    }

    public IEnumerable<Option> All =>
    [
        Target, Solution, Workspace, Config, Json, Out, DryRun, Apply, Yes, Quiet, Verbose, NoCache, NoLlm, Llm, FailOn, FailOnStale,
    ];

    public void AddTo(Command root)
    {
        foreach (var option in All)
        {
            root.Options.Add(option);
        }
    }

    /// <summary>
    /// Adds the cross-option rules to <paramref name="command"/> and every subcommand;
    /// a validator only runs for the command that was invoked, so each needs its own.
    /// </summary>
    public void AddValidators(Command command)
    {
        command.Validators.Add(result =>
        {
            if (result.GetValue(Llm) && result.GetValue(NoLlm))
            {
                result.AddError("--llm and --no-llm cannot be combined.");
            }

            if (result.GetValue(Apply) && result.GetValue(DryRun))
            {
                result.AddError("--apply and --dry-run cannot be combined.");
            }
        });
        foreach (var sub in command.Subcommands)
        {
            AddValidators(sub);
        }
    }

    public GlobalSettings Bind(ParseResult parse) => new()
    {
        Target = parse.GetValue(Target),
        Solution = parse.GetValue(Solution),
        Workspace = parse.GetValue(Workspace),
        Config = parse.GetValue(Config),
        Json = parse.GetValue(Json),
        Out = parse.GetValue(Out),
        DryRun = parse.GetValue(DryRun),
        Apply = parse.GetValue(Apply),
        Yes = parse.GetValue(Yes),
        Quiet = parse.GetValue(Quiet),
        Verbose = parse.GetValue(Verbose),
        NoCache = parse.GetValue(NoCache),
        Llm = parse.GetValue(Llm) ? true : parse.GetValue(NoLlm) ? false : null,
        FailOnStale = parse.GetValue(FailOnStale),
        FailOn = (parse.GetValue(FailOn) ?? "error") switch
        {
            "info" => Core.Diagnostics.FailOn.Info,
            "warning" => Core.Diagnostics.FailOn.Warning,
            "never" => Core.Diagnostics.FailOn.Never,
            _ => Core.Diagnostics.FailOn.Error,
        },
    };
}
