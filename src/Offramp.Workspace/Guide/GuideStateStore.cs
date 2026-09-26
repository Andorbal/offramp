using System.Text;
using System.Text.Json;
using Offramp.Core.Json;

namespace Offramp.Workspace.Guide;

/// <summary>Reads and writes <c>&lt;state&gt;/guide.json</c>, the guide's progress file.</summary>
public static class GuideStateStore
{
    public const string FileName = "guide.json";

    public static string PathIn(string stateDirectory) => Path.Combine(stateDirectory, FileName);

    /// <summary>The progress file, or null when there is none. Throws <see cref="InvalidDataException"/> when it cannot be read.</summary>
    public static GuideState? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        GuideState? state;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllText(path), WorkspaceJsonContext.Default.GuideState);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }

        if (state is null || state.Version != 1 || state.Records.Any(r => string.IsNullOrEmpty(r.Step)))
        {
            throw new InvalidDataException($"{FileName} is not a version 1 guide progress file.");
        }

        return state;
    }

    public static void Save(string path, GuideState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OfframpJson.Serialize(state, WorkspaceJsonContext.Default.GuideState), new UTF8Encoding(false));
    }
}
