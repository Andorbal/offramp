using Offramp.Cli.Infrastructure;
using Spectre.Console;

namespace Offramp.Cli.Rendering;

public static class ConsoleFactory
{
    /// <summary>True when colors must be off: <c>NO_COLOR</c>, <c>OFFRAMP_NO_COLOR</c>, or <c>TERM=dumb</c>.</summary>
    public static bool ColorsDisabled(CliHost host) =>
        Has(host, "NO_COLOR") || Has(host, "OFFRAMP_NO_COLOR")
        || (host.Environment.TryGetValue("TERM", out var term) && term == "dumb");

    /// <summary>A console over <paramref name="writer"/> honoring the host's terminal and color settings.</summary>
    public static IAnsiConsole Create(CliHost host, TextWriter writer, bool isTerminal, bool forceNoColor = false)
    {
        var noColor = forceNoColor || ColorsDisabled(host) || !isTerminal;
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = noColor ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Interactive = isTerminal && host.InputIsTerminal ? InteractionSupport.Yes : InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = host.Width;
        if (!isTerminal)
        {
            // Redirected output is read by tools and diffed in tests: plain, stable, UTF-8.
            console.Profile.Capabilities.Links = false;
        }

        return console;
    }

    private static bool Has(CliHost host, string name) =>
        host.Environment.TryGetValue(name, out var value) && value.Length > 0;
}
