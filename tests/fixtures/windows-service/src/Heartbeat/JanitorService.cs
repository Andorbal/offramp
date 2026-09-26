using System;
using System.ServiceProcess;
using System.Threading;

namespace Heartbeat
{
    /// <summary>Sweeps once a minute and reacts to logons: a second service in the same executable.</summary>
    public class JanitorService : ServiceBase
    {
        private Timer _sweep;

        public JanitorService()
        {
            ServiceName = "Janitor";
            CanHandleSessionChangeEvent = true;
        }

        protected override void OnStart(string[] args)
        {
            _sweep = new Timer(_ => Sweep(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        }

        protected override void OnStop()
        {
            _sweep.Dispose();
        }

        protected override void OnSessionChange(SessionChangeDescription changeDescription)
        {
            if (changeDescription.Reason == SessionChangeReason.SessionLogon)
            {
                Sweep();
            }
        }

        private static void Sweep()
        {
            Console.WriteLine("swept at " + DateTime.UtcNow.ToString("O"));
        }
    }
}
