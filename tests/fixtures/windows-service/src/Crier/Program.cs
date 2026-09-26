using Topshelf;

namespace Crier
{
    internal static class Program
    {
        private static void Main()
        {
            HostFactory.Run(x =>
            {
                x.Service<TownCrier>(s =>
                {
                    s.ConstructUsing(name => new TownCrier());
                    s.WhenStarted(tc => tc.Start());
                    s.WhenStopped(tc => tc.Stop());
                });
                x.RunAsLocalSystem();
                x.StartAutomatically();
                x.SetDescription("Announces the time every second.");
                x.SetDisplayName("Town Crier");
                x.SetServiceName("TownCrier");
            });
        }
    }
}
