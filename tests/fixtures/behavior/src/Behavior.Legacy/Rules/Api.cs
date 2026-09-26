using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// audit api: each class is named after its rule. Findings of that rule must land in a
// member named Positive*, and never in a member named Negative*.
namespace Behavior.Rules
{
    internal static class OFR3001
    {
        public static object Positive()
        {
            return System.Web.HttpContext.Current;
        }

        public static System.Web.UI.Control PositiveQualified()
        {
            return null;
        }

        public static object Negative()
        {
            return new List<int>();
        }
    }

    internal static class OFR3002
    {
        public static void Positive()
        {
            Console.Beep(800, 200);
        }

        public static void Negative()
        {
            Console.WriteLine();
        }
    }

    internal static class OFR3003
    {
        public static void Positive()
        {
            Thread.CurrentThread.Abort();
        }

        public static object PositiveDomain()
        {
            return AppDomain.CreateDomain("plugins");
        }

        public static void PositiveBeginInvoke()
        {
            Action work = () => { };
            work.BeginInvoke(null, null);
        }

        public static void Negative()
        {
            Task.Run(() => { });
        }
    }

    internal static class OFR3004
    {
        public class Positive : System.Web.UI.Page
        {
        }

        public class Negative
        {
        }
    }

    internal static class OFR3005
    {
        [System.Web.Services.WebService]
        public class Positive : System.Web.Services.WebService
        {
        }

        public class Negative
        {
            public string Hello()
            {
                return "hello";
            }
        }
    }

    internal static class OFR3006
    {
        public static object Positive()
        {
            return new System.ServiceModel.ServiceHost(typeof(object));
        }

        public static object Negative()
        {
            return new System.ServiceModel.EndpointAddress("http://localhost/service");
        }
    }

    internal static class OFR3007
    {
        public static void Positive()
        {
            System.Runtime.Remoting.RemotingConfiguration.Configure(null, false);
        }

        public static object Negative()
        {
            return new MarshalByRefObjectHolder();
        }

        private sealed class MarshalByRefObjectHolder
        {
        }
    }

    internal static class OFR3008
    {
        public static object Positive()
        {
            System.Activities.Activity activity = null;
            return activity;
        }

        public static object Negative()
        {
            return new object();
        }
    }

    internal static class OFR3009
    {
        public static object Positive()
        {
            return new System.Security.PermissionSet(System.Security.Permissions.PermissionState.None);
        }

        public static object Negative()
        {
            return new System.Security.SecurityException("denied");
        }
    }
}
