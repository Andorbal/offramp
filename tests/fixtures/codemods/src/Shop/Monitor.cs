using System.ServiceProcess;

namespace Shop
{
    public class Monitor
    {
        public bool SpoolerRunning()
        {
            using (var controller = new ServiceController("Spooler"))
            {
                return controller.Status == ServiceControllerStatus.Running;
            }
        }
    }
}
