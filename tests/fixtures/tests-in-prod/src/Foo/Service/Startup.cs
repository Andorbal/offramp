using Foo.Health;

namespace Foo.Service
{
    public static class Startup
    {
        public static bool Ready() => StartupChecks.Run();
    }
}
