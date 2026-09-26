using Offramp.Fixtures;

namespace Offramp.Ide.Tests;

/// <summary>docs/spec/commands/ide.md#when-it-is-on.</summary>
public sealed class EnablementTests
{
    [Fact]
    public void Auto_follows_the_offramp_directory_and_the_setting_overrides_it()
    {
        using var repository = new ScratchDirectory("ide-enablement");

        var without = Enablement.Resolve(repository.Path, "auto");
        var forced = Enablement.Resolve(repository.Path, "on");
        Directory.CreateDirectory(repository.Combine(".offramp"));
        var with = Enablement.Resolve(repository.Path, null);
        var disabled = Enablement.Resolve(repository.Path, "off");

        Assert.Equal(new IdeEnablement("auto", false, "no-state-directory"), without);
        Assert.Equal(new IdeEnablement("on", true, "setting-on"), forced);
        Assert.Equal(new IdeEnablement("auto", true, "state-directory"), with);
        Assert.Equal(new IdeEnablement("off", false, "setting-off"), disabled);
    }
}
