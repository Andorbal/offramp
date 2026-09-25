using System.Runtime.CompilerServices;
using DiffEngine;
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
    }
}
