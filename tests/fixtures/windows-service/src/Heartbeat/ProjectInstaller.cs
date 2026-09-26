using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace Heartbeat
{
    [RunInstaller(true)]
    public class ProjectInstaller : Installer
    {
        public ProjectInstaller()
        {
            var process = new ServiceProcessInstaller();
            process.Account = ServiceAccount.LocalSystem;

            var heartbeat = new ServiceInstaller();
            heartbeat.ServiceName = "Heartbeat";
            heartbeat.DisplayName = "Heartbeat";
            heartbeat.Description = "Records a beat every few seconds.";
            heartbeat.StartType = ServiceStartMode.Automatic;
            heartbeat.ServicesDependedOn = new[] { "EventLog" };

            var janitor = new ServiceInstaller();
            janitor.ServiceName = "Janitor";
            janitor.StartType = ServiceStartMode.Manual;

            Installers.AddRange(new Installer[] { process, heartbeat, janitor });
        }
    }
}
