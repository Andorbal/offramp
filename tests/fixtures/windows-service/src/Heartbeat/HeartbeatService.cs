using System;
using System.Configuration;
using System.Diagnostics;
using System.ServiceProcess;
using System.Timers;

namespace Heartbeat
{
    /// <summary>Records a beat every few seconds.</summary>
    public partial class HeartbeatService : ServiceBase
    {
        private readonly Timer _timer = new Timer();
        private readonly Pulse _pulse = new Pulse();
        private bool _paused;

        public HeartbeatService()
        {
            InitializeComponent();
            CanPauseAndContinue = true;
        }

        protected override void OnStart(string[] args)
        {
            _pulse.Reset(ConfigurationManager.AppSettings["Source"] ?? "heartbeat");
            _timer.Interval = 5000;
            _timer.Elapsed += OnElapsed;
            _timer.Start();
            EventLog.WriteEntry("Heartbeat started.");
        }

        protected override void OnStop()
        {
            _timer.Stop();
            RequestAdditionalTime(2000);
            _pulse.Flush();
            EventLog.WriteEntry("Heartbeat stopped after " + _pulse.Count + " beats.", EventLogEntryType.Warning);
        }

        protected override void OnPause()
        {
            _paused = true;
        }

        protected override void OnContinue()
        {
            _paused = false;
        }

        private void OnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_paused)
            {
                return;
            }

            _pulse.Beat(DateTime.UtcNow);
            Trace.WriteLine("beat " + _pulse.Count);
        }
    }
}
