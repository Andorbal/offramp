using Offramp.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Offramp.Cli.Rendering;

/// <summary>
/// The human view on stdout. Wraps a Spectre console configured for the host
/// (colors off with <c>NO_COLOR</c>, <c>TERM=dumb</c>, or redirected output).
/// </summary>
public sealed class HumanOutput(IAnsiConsole console)
{
    public IAnsiConsole Console { get; } = console;

    public bool Unicode => Console.Profile.Capabilities.Unicode;

    /// <summary>The one-sentence outcome, first line of output.</summary>
    public void Headline(string text, string style) =>
        Console.MarkupLine($"[bold {style}]{Markup.Escape(text)}[/]");

    public void Line(string text = "") => Console.WriteLine(text);

    public void MarkupLine(string markup) => Console.MarkupLine(markup);

    public void Write(IRenderable renderable) => Console.Write(renderable);

    public string Icon(Severity severity) => severity switch
    {
        Severity.Error => Unicode ? "✗" : "x",
        Severity.Warning => "!",
        _ => Unicode ? "·" : "-",
    };

    public static string Style(Severity severity) => severity switch
    {
        Severity.Error => Theme.BlockingStyle,
        Severity.Warning => Theme.DecisionStyle,
        _ => Theme.DimStyle,
    };

    /// <summary>Lists diagnostics, most severe first, each with code, location, and message.</summary>
    public void Diagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        foreach (var d in diagnostics.OrderByDescending(d => d.Severity).ThenBy(d => d, DiagnosticOrder.Instance))
        {
            var location = FormatLocation(d);
            var prefix = $"[{Style(d.Severity)}]{Markup.Escape(Icon(d.Severity))} {d.Code}[/]";
            var where = location is null ? "" : $" [dim]{Markup.Escape(location)}[/]";
            var overridden = d.Overridden ? " [dim](severity overridden in offramp.yml)[/]" : "";
            Console.MarkupLine($"{prefix}{where} {Markup.Escape(d.Message)}{overridden}");
        }
    }

    /// <summary>The closing line: counts and, when obvious, the next command to run.</summary>
    public void Summary(DiagnosticSummary summary, string? next)
    {
        var parts = new List<string>();
        if (summary.Errors > 0) parts.Add($"[{Theme.BlockingStyle}]{summary.Errors} error(s)[/]");
        if (summary.Warnings > 0) parts.Add($"[{Theme.DecisionStyle}]{summary.Warnings} warning(s)[/]");
        if (summary.Info > 0) parts.Add($"[{Theme.DimStyle}]{summary.Info} info[/]");
        if (parts.Count > 0 || next is not null)
        {
            Console.WriteLine();
        }

        if (parts.Count > 0)
        {
            Console.MarkupLine(string.Join(", ", parts));
        }

        if (next is not null)
        {
            Console.MarkupLine($"[dim]Next:[/] {Markup.Escape(next)}");
        }
    }

    private static string? FormatLocation(Diagnostic d)
    {
        var path = d.File ?? d.Project;
        if (path is null)
        {
            return null;
        }

        return d.Line is null ? path : d.Column is null ? $"{path}:{d.Line}" : $"{path}:{d.Line}:{d.Column}";
    }
}
