using Spectre.Console;

namespace Offramp.Cli.Rendering;

/// <summary>
/// Colors carry meaning: red blocks, yellow needs a decision, green is ready,
/// dim is informational. Framework classes use the fixed palette shared with
/// the HTML outputs (<c>docs/spec/01-cli-conventions.md</c>).
/// </summary>
public static class Theme
{
    public static readonly Color Blocking = Color.Red;
    public static readonly Color Decision = Color.Yellow;
    public static readonly Color Ready = Color.Green;

    public static readonly Color FrameworkOrange = new(0xE0, 0x7A, 0x1F);
    public static readonly Color StandardBlue = new(0x2B, 0x6C, 0xB0);
    public static readonly Color ModernGreen = new(0x2F, 0x85, 0x5A);
    public static readonly Color DualTeal = new(0x2C, 0x7A, 0x7B);

    public const string BlockingStyle = "red";
    public const string DecisionStyle = "yellow";
    public const string ReadyStyle = "green";
    public const string DimStyle = "dim";
}
