using Offramp.Cli.Rendering;
using Offramp.Core.Configuration;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Init;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary>
/// The <c>init</c> interview: target, solution, verify mode, CPM file, and pins, with detected
/// defaults. Each question says what the setting does and what most people answer; typed paths
/// and package ids are checked before they reach offramp.yml.
/// </summary>
public sealed class SpectreInitPrompter(IAnsiConsole console) : IInitPrompter
{
    private const string NoSolution = "(none)";

    private static readonly Dictionary<string, string> VerifyChoices = new(StringComparer.Ordinal)
    {
        ["build"] = "build    Build the projects a change touches with dotnet build (recommended)",
        ["command"] = "command  Run a command you choose instead, such as your build script",
        ["none"] = "none     Do not check (changes that break the build are kept)",
    };

    public InitValues Ask(InitDetection detection)
    {
        var detected = detection.Values;
        var root = detection.RepositoryRoot;
        Explain("Offramp writes your answers to offramp.yml, which you can edit later. Press Enter to keep a suggested answer.");

        Explain("The modern .NET your projects are moving to, as a major version: 10 means .NET 10 (net10.0).");
        var target = console.Prompt(new TextPrompt<int>("Target .NET major version?")
            .DefaultValue(detected.Target)
            .Validate(v => v >= 5 ? ValidationResult.Success() : ValidationResult.Error("Use 5 or higher.")));

        var solution = detected.Solution;
        if (detection.SolutionCandidates.Count > 0)
        {
            Explain("The solution Offramp builds to learn your projects and how they depend on each other.");
            var choices = detection.SolutionCandidates.Append(NoSolution).ToList();
            var prompt = new SelectionPrompt<string>()
                .Title("Which solution should Offramp work on?")
                .AddChoices(choices);
            if (solution is not null)
            {
                prompt.DefaultValue(solution);
            }

            var picked = console.Prompt(prompt);
            solution = picked == NoSolution ? null : picked;
        }

        Explain("Offramp checks every change it writes (moved files, package updates, code fixes) and undoes the change if the check fails.");
        var verify = console.Prompt(new SelectionPrompt<string>()
            .Title("How should Offramp check its changes?")
            .AddChoices(VerifyChoices.Keys)
            .UseConverter(choice => Markup.Escape(VerifyChoices[choice]))
            .DefaultValue(detected.VerifyMode));
        string? verifyCommand = null;
        if (verify == "command")
        {
            var commandPrompt = new TextPrompt<string>("Command to run? (from the repository root; exit code 0 means the change is good)");
            if (detected.VerifyCommand is not null)
            {
                commandPrompt.DefaultValue(detected.VerifyCommand);
            }

            verifyCommand = console.Prompt(commandPrompt).Trim();
        }

        var (cpmFile, cpmScope) = AskCpmFile(root, detected, solution);
        var pins = AskPins(root, detected.Pins);

        return new InitValues
        {
            Target = target,
            Solution = solution,
            VerifyMode = verify,
            VerifyCommand = verifyCommand,
            CpmFile = cpmFile,
            CpmScope = cpmScope,
            Pins = pins,
        };
    }

    public bool OfferCompileOnlyBlock(CompileOnlyFix plan)
    {
        console.MarkupLine("Some projects need Windows to build. Offramp can add this block so macOS and Linux builds skip those steps:");
        DiffRenderer.Render(new HumanOutput(console), plan.Diff ?? "");
        return console.Confirm($"Add it to {plan.File}?", defaultValue: false);
    }

    /// <summary>The suggestion is the configured file resolved for the chosen solution; a typed path is taken from the repository root.</summary>
    private (string File, string Scope) AskCpmFile(string root, InitValues detected, string? solution)
    {
        Explain("Central package management keeps every NuGet package version in one .props file instead of in each project. "
            + "Offramp creates that file only when you run `offramp deps consolidate --cpm`; nothing is written now. "
            + "If the repository has projects outside this solution, choose a folder that holds only this solution's projects: "
            + "a Directory.Packages.props applies to every project below it.");
        var suggested = new CpmConfig { File = detected.CpmFile, Scope = detected.CpmScope }.PathFor(solution);
        var answer = console.Prompt(new TextPrompt<string>("Central package versions file? (path from the repository root)")
            .DefaultValue(suggested)
            .Validate(a => Check(InitAnswers.CpmFile(root, a))));
        if (answer == suggested)
        {
            return (detected.CpmFile, detected.CpmScope);
        }

        var location = InitAnswers.CpmFile(root, answer).Value!;
        return (location.File, location.Scope);
    }

    private List<PackagePin> AskPins(string root, IReadOnlyList<PackagePin> detected)
    {
        Explain("A pin holds a NuGet package at one version, so Offramp never upgrades or consolidates it; for example a package "
            + "customers depend on. Most people answer No and add pins to offramp.yml later.");
        var pins = detected.ToList();
        while (console.Confirm(pins.Count == 0 ? "Pin a package to a version?" : "Pin another package?", defaultValue: false))
        {
            var package = console.Prompt(new TextPrompt<string>("  Package id, as nuget.org shows it (for example Newtonsoft.Json)?")
                .Validate(a => Check(InitAnswers.PackageId(a))));
            var version = console.Prompt(new TextPrompt<string>("  Version to hold it at (for example 9.0.1)?")
                .Validate(a => Check(InitAnswers.Version(a))));
            var project = console.Prompt(new TextPrompt<string>("  Only in one project? Its .csproj path, or Enter for every project:")
                .AllowEmpty()
                .Validate(a => Check(InitAnswers.Project(root, a))));
            var reason = console.Ask<string>("  Why is it pinned? (kept in offramp.yml for your team)");
            pins.Add(new PackagePin
            {
                Package = InitAnswers.PackageId(package).Value!,
                Version = InitAnswers.Version(version).Value!,
                Project = InitAnswers.Project(root, project).Value,
                Reason = reason.Trim(),
            });
        }

        return pins;
    }

    private void Explain(string text)
    {
        console.WriteLine();
        console.MarkupLine($"[dim]{Markup.Escape(text)}[/]");
    }

    private static ValidationResult Check<T>(InitAnswer<T> answer) =>
        answer.Error is null ? ValidationResult.Success() : ValidationResult.Error(Markup.Escape(answer.Error));
}
