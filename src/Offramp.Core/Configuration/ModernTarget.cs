using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Core.Model;

namespace Offramp.Core.Configuration;

/// <summary>
/// The target a migration aims at (<c>target:</c>, <c>--target</c>, <c>OFFRAMP_TARGET</c>;
/// docs/decisions/0057-a-target-is-a-framework.md): a modern .NET (<c>net10.0</c>), the same on Windows only
/// (<c>net10.0-windows</c>), or .NET Standard (<c>netstandard2.0</c>, <c>netstandard2.1</c>) for a library that serves
/// .NET Framework and modern .NET from one build. An integer is shorthand for <c>netN.0</c>. The value is written
/// back as it was given, so <c>effectiveConfig</c> shows <c>10</c> for 10 and <c>"netstandard2.0"</c> for that.
/// </summary>
[JsonConverter(typeof(ModernTargetJsonConverter))]
public sealed class ModernTarget : IEquatable<ModernTarget>
{
    /// <summary>The built-in default, and the .NET that code which must run moves to under a .NET Standard target.</summary>
    public const int DefaultMajor = 10;

    /// <summary>The oldest .NET that is a target (<c>net5.0</c>).</summary>
    public const int MinimumMajor = 5;

    /// <summary>What a target may be, for messages.</summary>
    public const string Expected = "a .NET major version (10), a .NET target framework (net8.0, net10.0-windows), or .NET Standard (netstandard2.0, netstandard2.1)";

    private const string WindowsSuffix = "-windows";

    private ModernTarget(string moniker, int? major, bool windows, string written)
    {
        Moniker = moniker;
        Major = major;
        Windows = windows;
        Written = written;
    }

    /// <summary><c>net10.0</c> as the integer 10.</summary>
    public static ModernTarget Default { get; } = FromMajor(DefaultMajor);

    /// <summary><c>net10.0</c>, <c>net10.0-windows</c>, or <c>netstandard2.0</c>.</summary>
    public string Moniker { get; }

    /// <summary>The .NET major version; null for .NET Standard.</summary>
    public int? Major { get; }

    public bool IsStandard => Major is null;

    /// <summary>True for <c>netN.0-windows</c>: every project moves to .NET on Windows.</summary>
    public bool Windows { get; }

    /// <summary>The value as given: <c>10</c> or <c>netstandard2.0</c>.</summary>
    public string Written { get; }

    /// <summary>True when the value was given as an integer.</summary>
    public bool WrittenAsNumber => Written.All(char.IsAsciiDigit);

    /// <summary>
    /// The .NET major version code runs on: <see cref="Major"/>, or <see cref="DefaultMajor"/> under .NET Standard,
    /// which runs nowhere by itself: applications, tests, and new projects need a .NET, and a .NET Standard library
    /// runs on every one, the newest included.
    /// </summary>
    public int RuntimeMajor => Major ?? DefaultMajor;

    /// <summary><c>net10.0</c> for 10.</summary>
    public static ModernTarget FromMajor(int major) =>
        new(Net(major), major, windows: false, major.ToString(CultureInfo.InvariantCulture));

    /// <summary>Parses a target, or throws <see cref="FormatException"/>.</summary>
    public static ModernTarget Parse(string text) =>
        TryParse(text, out var target) ? target : throw new FormatException($"'{text}' is not a target: expected {Expected}.");

    /// <summary>
    /// An integer of at least 5 (<c>10</c>), <c>netN.0</c> or <c>netN.0-windows</c> with N of at least 5, or
    /// <c>netstandard2.0</c> or <c>netstandard2.1</c>; lowercase, as NuGet writes them.
    /// </summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ModernTarget? target)
    {
        target = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.All(char.IsAsciiDigit))
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major >= MinimumMajor && text[0] != '0')
            {
                target = FromMajor(major);
            }

            return target is not null;
        }

        if (text is "netstandard2.0" or "netstandard2.1")
        {
            target = new ModernTarget(text, null, windows: false, text);
            return true;
        }

        var windows = text.EndsWith(WindowsSuffix, StringComparison.Ordinal);
        var core = windows ? text[..^WindowsSuffix.Length] : text;
        if (core.StartsWith("net", StringComparison.Ordinal) && core.EndsWith(".0", StringComparison.Ordinal))
        {
            var digits = core[3..^2];
            if (digits.Length is > 0 and <= 3 && digits[0] != '0' && digits.All(char.IsAsciiDigit)
                && int.Parse(digits, CultureInfo.InvariantCulture) is var major && major >= MinimumMajor)
            {
                target = new ModernTarget(text, major, windows, text);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The framework <paramref name="project"/> moves to under this target (ADR 0057):
    /// <list type="bullet">
    /// <item><c>netN.0</c>: <c>netN.0-windows</c> for a project that uses Windows Forms or WPF, which run nowhere
    /// else, and <c>netN.0</c> for the others.</item>
    /// <item><c>netN.0-windows</c>: <c>netN.0-windows</c> for every project.</item>
    /// <item><c>netstandard2.x</c>: the standard for a library; a project that runs (an application or a test
    /// project) needs a .NET, <c>net10.0</c>, or <c>net10.0-windows</c> with Windows Forms or WPF.</item>
    /// </list>
    /// </summary>
    public string For(ProjectInfo project)
    {
        var desktop = WindowsDesktop.Uses(project);
        if (IsStandard)
        {
            return desktop ? Net(DefaultMajor) + WindowsSuffix : Runs(project) ? Net(DefaultMajor) : Moniker;
        }

        return desktop || Windows ? Net(Major!.Value) + WindowsSuffix : Net(Major!.Value);
    }

    /// <summary>An application or a test project: code that needs a runtime, which .NET Standard is not.</summary>
    public static bool Runs(ProjectInfo project) =>
        project.Kind is ProjectKind.Console or ProjectKind.Service or ProjectKind.Web or ProjectKind.Winforms or ProjectKind.Wpf or ProjectKind.Test;

    public bool Equals(ModernTarget? other) => other is not null && string.Equals(Moniker, other.Moniker, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ModernTarget);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Moniker);

    public override string ToString() => Moniker;

    private static string Net(int major) => string.Create(CultureInfo.InvariantCulture, $"net{major}.0");
}

/// <summary>Reads a target from an integer or a string, and writes it back the way it was given.</summary>
public sealed class ModernTargetJsonConverter : JsonConverter<ModernTarget>
{
    public override ModernTarget Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var major) ? major.ToString(CultureInfo.InvariantCulture) : null,
            JsonTokenType.String => reader.GetString(),
            _ => null,
        };
        return ModernTarget.TryParse(text, out var target)
            ? target
            : throw new JsonException($"target must be {ModernTarget.Expected}.");
    }

    public override void Write(Utf8JsonWriter writer, ModernTarget value, JsonSerializerOptions options)
    {
        if (value.WrittenAsNumber)
        {
            writer.WriteNumberValue(value.Major!.Value);
        }
        else
        {
            writer.WriteStringValue(value.Written);
        }
    }
}
