using System.ServiceProcess;

namespace Heartbeat
{
    internal static class Program
    {
        private static void Main()
        {
            ServiceBase.Run(new ServiceBase[] { new HeartbeatService(), new JanitorService() });
        }
    }
}
