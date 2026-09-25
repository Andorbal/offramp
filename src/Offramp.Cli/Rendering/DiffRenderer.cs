using Spectre.Console;

namespace Offramp.Cli.Rendering;

/// <summary>Renders a unified diff with colored file headers, hunks, additions, and removals.</summary>
public static class DiffRenderer
{
    public static void Render(HumanOutput output, string diff)
    {
        foreach (var line in diff.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var escaped = Markup.Escape(line);
            var style = line switch
            {
                _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) => "bold",
                _ when line.StartsWith("@@", StringComparison.Ordinal) => "cyan",
                _ when line.StartsWith('+') => Theme.ReadyStyle,
                _ when line.StartsWith('-') => Theme.BlockingStyle,
                _ => null,
            };
            output.MarkupLine(style is null ? escaped : $"[{style}]{escaped}[/]");
        }
    }
}
