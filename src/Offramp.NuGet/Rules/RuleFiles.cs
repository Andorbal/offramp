using System.Text.Json.Nodes;
using Offramp.Core.Configuration;

namespace Offramp.NuGet.Rules;

/// <summary>Rule tables shipped as <c>rules/*.yml</c> and embedded in the assembly.</summary>
internal static class RuleFiles
{
    public static JsonNode Load(string name)
    {
        using var stream = typeof(RuleFiles).Assembly.GetManifestResourceStream("Offramp.NuGet.Rules." + name)
            ?? throw new InvalidOperationException($"Missing embedded rule file {name}.");
        using var reader = new StreamReader(stream);
        return YamlJson.Parse(reader.ReadToEnd()).Root;
    }
}
