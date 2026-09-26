using System.Runtime.CompilerServices;
using DiffEngine;
using EmptyFiles;
using VerifyTests;

namespace Offramp.Fixtures;

public static class Snapshots
{
    /// <summary>Call from each test assembly's module initializer.</summary>
    public static void Initialize()
    {
        DiffRunner.Disabled = true;
        VerifierSettings.DontScrubDateTimes();
        VerifierSettings.DontScrubGuids();
        VerifierSettings.UseStrictJson();
        Verifier.UseProjectRelativeDirectory("Snapshots");
        foreach (var extension in new[] { "dot", "mmd", "md", "html", "yml", "slnf" })
        {
            if (!FileExtensions.IsTextExtension(extension))
            {
                FileExtensions.AddTextExtension(extension);
            }
        }
    }
}
