using System.Threading;

namespace Shop
{
    public class Poller
    {
        private Thread _thread;

        public void Start()
        {
            _thread = new Thread(Run);
            _thread.Start();
        }

        public void Stop()
        {
            _thread.Abort();
        }

        private void Run()
        {
            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
