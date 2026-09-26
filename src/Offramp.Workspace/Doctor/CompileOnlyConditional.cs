using System.Text;

namespace Offramp.Workspace.Doctor;

/// <summary>What <c>doctor --fix</c> would do, or did, to Directory.Build.props.</summary>
public sealed record CompileOnlyFix
{
    /// <summary>Repository-relative path of the props file.</summary>
    public required string File { get; init; }

    /// <summary>True when the block is already there; nothing to do.</summary>
    public required bool AlreadyPresent { get; init; }

    /// <summary>True when the file was written in this run.</summary>
    public required bool Applied { get; init; }

    /// <summary>The unified diff of the change, or null when nothing changes.</summary>
    public string? Diff { get; init; }
}

/// <summary>
/// The compile-only conditional of docs/compiling-on-macos.md, inserted into the
/// repository's root Directory.Build.props as text, so every other byte of the
/// file (formatting, comments, line endings) stays as it was.
/// </summary>
public static class CompileOnlyConditional
{
    public const string FileName = "Directory.Build.props";
    public const string Marker = "<OfframpCompileOnly>";

    public static readonly string[] BlockLines =
    [
        "<!-- Compile-only builds on macOS/Linux: skip steps that need Windows (added by offramp doctor). -->",
        "<PropertyGroup Condition=\"!$([MSBuild]::IsOSPlatform('Windows'))\">",
        "  <GenerateSerializationAssemblies>Off</GenerateSerializationAssemblies>",
        "  <EnableWindowsTargeting>true</EnableWindowsTargeting>",
        "  <OfframpCompileOnly>true</OfframpCompileOnly>",
        "</PropertyGroup>",
    ];

    /// <summary>The new content for <paramref name="current"/> (null when the file does not exist), or null when already present.</summary>
    public static string? Apply(string? current)
    {
        if (current is not null && current.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        if (current is null)
        {
            var created = new StringBuilder("<Project>\n");
            foreach (var line in BlockLines)
            {
                created.Append("  ").Append(line).Append('\n');
            }

            return created.Append("</Project>\n").ToString();
        }

        var newline = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var close = current.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (close < 0)
        {
            throw new InvalidDataException($"{FileName} has no closing </Project> element.");
        }

        var lineStart = current.LastIndexOf('\n', Math.Max(close - 1, 0)) + 1;
        var closingIndent = current[lineStart..close];
        var indent = closingIndent.Trim().Length == 0 ? closingIndent + "  " : "  ";
        var insertion = new StringBuilder();
        if (lineStart != close || close == 0)
        {
            // "</Project>" shares its line with other content; start the block on a new line.
            insertion.Append(newline);
        }

        insertion.Append(indent).Append(BlockLines[0]).Append(newline);
        foreach (var line in BlockLines.Skip(1))
        {
            insertion.Append(indent).Append(line).Append(newline);
        }

        var insertAt = closingIndent.Trim().Length == 0 ? lineStart : close;
        if (insertAt == close && lineStart != close)
        {
            insertion.Append(closingIndent.Trim().Length == 0 ? closingIndent : "");
        }

        return current[..insertAt] + insertion + current[insertAt..];
    }
}
