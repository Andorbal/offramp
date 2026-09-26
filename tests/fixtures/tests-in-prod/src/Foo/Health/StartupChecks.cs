using Xunit;

namespace Foo.Health
{
    /// <summary>A test class that production code also calls, so it cannot move.</summary>
    public class StartupChecks
    {
        public static bool Run() => new StartupChecks().Passes();

        [Fact]
        public void Clock_is_sane() => Assert.True(Passes());

        private bool Passes() => System.DateTime.Today.Year > 2000;
    }
}
