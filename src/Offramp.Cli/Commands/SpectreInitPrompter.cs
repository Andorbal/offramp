using Offramp.Cli.Rendering;
using Offramp.Core.Configuration;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Init;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary>The <c>init</c> interview: target, solution, verify mode, CPM file, and pins, with detected defaults.</summary>
public sealed class SpectreInitPrompter(IAnsiConsole console) : IInitPrompter
{
    private const string NoSolution = "(none)";

    public InitValues Ask(InitDetection detection)
    {
        var detected = detection.Values;
        var target = console.Prompt(new TextPrompt<int>("Target .NET major version?")
            .DefaultValue(detected.Target)
            .Validate(v => v >= 5 ? ValidationResult.Success() : ValidationResult.Error("Use 5 or higher.")));

        var solution = detected.Solution;
        if (detection.SolutionCandidates.Count > 0)
        {
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

        var verify = console.Prompt(new SelectionPrompt<string>()
            .Title("How should Offramp verify changes?")
            .AddChoices("build", "command", "none")
            .DefaultValue(detected.VerifyMode));

        var cpm = console.Prompt(new TextPrompt<string>("Central package management file?")
            .DefaultValue(detected.CpmFile));

        var pins = detected.Pins.ToList();
        while (console.Confirm("Add a package version pin?", defaultValue: false))
        {
            var package = console.Ask<string>("  Package id?");
            var version = console.Ask<string>("  Version?");
            var project = console.Prompt(new TextPrompt<string>("  Project (empty = everywhere)?").AllowEmpty());
            var reason = console.Ask<string>("  Why is it pinned?");
            pins.Add(new PackagePin
            {
                Package = package,
                Version = version,
                Project = string.IsNullOrWhiteSpace(project) ? null : project,
                Reason = reason,
            });
        }

        return new InitValues
        {
            Target = target,
            Solution = solution,
            VerifyMode = verify,
            CpmFile = cpm,
            Pins = pins,
        };
    }

    public bool OfferCompileOnlyBlock(CompileOnlyFix plan)
    {
        console.MarkupLine("Some projects need Windows to build. Offramp can add this block so macOS and Linux builds skip those steps:");
        DiffRenderer.Render(new HumanOutput(console), plan.Diff ?? "");
        return console.Confirm($"Add it to {plan.File}?", defaultValue: false);
    }
}
