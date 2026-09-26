using System.Configuration;

namespace Shop
{
    public interface IClock
    {
        System.DateTime Now { get; }
    }

    public class Mailer
    {
        private readonly IClock _clock;

        public Mailer(IClock clock)
        {
            _clock = clock;
        }

        public string Host()
        {
            return ConfigurationManager.AppSettings["SmtpHost"] + " at " + _clock.Now;
        }

        public static string DefaultSender()
        {
            return ConfigurationManager.AppSettings["Sender"];
        }
    }
}
