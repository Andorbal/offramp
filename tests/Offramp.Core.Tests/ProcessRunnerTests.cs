using Offramp.Core.Processes;

namespace Offramp.Core.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Raw_arguments_reach_the_process_without_added_quoting()
    {
        var (file, raw) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/d /s /c \"echo \"a b\" \"c d\"\"")
            : ("/bin/sh", "-c 'echo \"a b\" \"c d\"'");

        var result = await new ProcessRunner().RunAsync(new ProcessSpec(file, []) { RawArguments = raw });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("a b", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("c d", result.StandardOutput, StringComparison.Ordinal);
    }
}
