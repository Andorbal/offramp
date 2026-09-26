namespace Offramp.Reporting;

/// <summary>Templates embedded in the assembly (<c>Resources/</c>).</summary>
internal static class Resources
{
    public static string Read(string name)
    {
        using var stream = typeof(Resources).Assembly.GetManifestResourceStream("Offramp.Reporting.Resources." + name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
