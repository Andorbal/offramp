using System.Net;
using System.Threading.Tasks;

namespace Shop
{
    public class Feeds
    {
        public async Task<int> LengthAsync(string url)
        {
            using (var client = new WebClient())
            {
                var text = client.DownloadString(url);
                await Task.Yield();
                return text.Length;
            }
        }

        public string Read(string url)
        {
            using (var client = new WebClient())
            {
                return client.DownloadString(url);
            }
        }
    }
}
