using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// audit behavior: each class is named after its rule (OFR3116 is app.config's).
namespace Behavior.Rules
{
    internal static class OFR3101
    {
        public static bool Positive(string name)
        {
            return name.StartsWith("a");
        }

        public static IEnumerable<string> PositiveOrdering(IEnumerable<string> names)
        {
            return names.OrderBy(n => n);
        }

        public static bool Negative(string name)
        {
            return name.StartsWith("a", StringComparison.Ordinal) && name.ToUpperInvariant() == "A";
        }

        public static IEnumerable<string> NegativeOrdering(IEnumerable<string> names)
        {
            return names.OrderBy(n => n, StringComparer.Ordinal);
        }
    }

    internal static class OFR3102
    {
        public static Encoding Positive()
        {
            return Encoding.GetEncoding(1252);
        }

        public static Encoding Negative()
        {
            return Encoding.GetEncoding(65001);
        }
    }

    internal static class OFR3103
    {
        public static string Positive()
        {
            return File.ReadAllText("C:\\data\\input.txt");
        }

        public static string PositiveFolder()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Cookies);
        }

        public static string Negative()
        {
            return File.ReadAllText(Path.Combine("data", "input.txt")) + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
    }

    internal static class OFR3104
    {
        public static TimeZoneInfo Positive()
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }

        public static TimeZoneInfo Negative()
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }

    internal static class OFR3105
    {
        public static object Positive()
        {
            return Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\Software\\Behavior", "Mode", null);
        }

        public static object Negative()
        {
            return Environment.GetEnvironmentVariable("BEHAVIOR_MODE");
        }
    }

    internal static class OFR3106
    {
        public static object Positive()
        {
            return System.Web.HttpRuntime.AppDomainAppPath;
        }

        public static object Negative()
        {
            return new System.Web.HttpCookie("mode");
        }
    }

    internal static class OFR3107
    {
        public static void Positive()
        {
            Process.Start("https://example.com");
        }

        public static void Negative()
        {
            Process.Start(new ProcessStartInfo("https://example.com") { UseShellExecute = true });
        }
    }

    internal static class OFR3108
    {
        public static object Positive()
        {
            return new System.Data.SqlClient.SqlConnection();
        }

        public static object Negative()
        {
            return new System.Data.DataTable();
        }
    }

    internal static class OFR3109
    {
        public static string Positive(double value)
        {
            return value.ToString();
        }

        public static string Negative(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    internal static class OFR3110
    {
        public static object Positive()
        {
            return new WebClient();
        }

        public static object Negative()
        {
            return new System.Net.Http.HttpClient();
        }
    }

    internal static class OFR3111
    {
        public static object Positive()
        {
            return Thread.CurrentPrincipal;
        }

        public static object Negative()
        {
            return Thread.CurrentThread.Name;
        }
    }

    internal static class OFR3112
    {
        public static string Positive()
        {
            return ConfigurationManager.AppSettings["mode"];
        }

        public static void Negative()
        {
            ConfigurationManager.RefreshSection("appSettings");
        }
    }

    internal static class OFR3113
    {
        public static bool Positive(string input)
        {
            return new Regex("a+b").IsMatch(input);
        }

        public static bool Negative(string input)
        {
            return new Regex("a+b", RegexOptions.None, TimeSpan.FromSeconds(1)).IsMatch(input);
        }
    }

    internal static class OFR3114
    {
        public static SslProtocols Positive()
        {
            return SslProtocols.Tls;
        }

        public static SslProtocols Negative()
        {
            return SslProtocols.Tls12;
        }
    }

    internal static class OFR3115
    {
        public static Assembly Positive()
        {
            return Assembly.LoadFrom("plugin.dll");
        }

        public static Assembly Negative()
        {
            return Assembly.Load("Plugin");
        }
    }

    internal static class OFR3117
    {
        public static string Positive(string value)
        {
            return System.Web.HttpUtility.UrlEncode(value);
        }

        public static string Negative(string value)
        {
            return WebUtility.UrlEncode(value);
        }
    }

    internal static class OFR3118
    {
        public static byte[] Positive(byte[] data)
        {
            return System.Web.Security.MachineKey.Protect(data);
        }

        public static object Negative()
        {
            return new System.Web.HttpCookie("session");
        }
    }

    internal static class OFR3119
    {
        public static object Positive()
        {
            return new System.Timers.Timer(1000);
        }

        public static object Negative()
        {
            return new System.Threading.Timer(_ => { });
        }
    }

    internal static class OFR3120
    {
        public static object Positive()
        {
            return Environment.OSVersion.Platform;
        }

        public static object Negative()
        {
            return Environment.ProcessorCount;
        }
    }
}
