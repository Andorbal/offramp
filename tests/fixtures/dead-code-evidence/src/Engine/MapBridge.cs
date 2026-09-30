using System.Runtime.InteropServices;

namespace Evidence.Engine
{
    /// <summary>
    /// The object a WebBrowser control gives its page as window.external (ObjectForScripting), as Open
    /// Live Writer's JSMapController is: the page's scripts call its public methods.
    /// </summary>
    [ComVisible(true)]
    public sealed class MapBridge
    {
        /// <summary>Called from map.html.</summary>
        public void NextEvent()
        {
            System.Console.WriteLine("next");
        }

        /// <summary>Called from scripts/map.js.</summary>
        public void JsUpdateBirdsEye()
        {
            System.Console.WriteLine("birds eye");
        }

        /// <summary>Called by no page in the repository; COM clients may.</summary>
        public void SetCenter(double latitude, double longitude)
        {
            System.Console.WriteLine(latitude + longitude);
        }
    }

    public sealed class PlainBridge
    {
        /// <summary>Named only in a minified library script, which is not read.</summary>
        public void Ping()
        {
            System.Console.WriteLine("ping");
        }
    }
}
