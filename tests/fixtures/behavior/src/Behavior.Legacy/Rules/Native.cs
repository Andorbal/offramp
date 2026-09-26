using System;
using System.Runtime.InteropServices;

// audit native: each class is named after its rule.
namespace Behavior.Rules
{
    internal static class OFR3301
    {
        [DllImport("kernel32.dll")]
        public static extern uint Positive();

        public static uint Negative()
        {
            return 0;
        }
    }

    internal static class OFR3302
    {
        [DllImport("user32.dll", EntryPoint = "MessageBox")]
        public static extern int Positive(IntPtr owner, string text, string caption, uint type);

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        public static extern int Negative(IntPtr owner, string text, string caption, uint type);
    }

    internal static class OFR3303
    {
        [DllImport("libc", EntryPoint = "abs")]
        public static extern int Positive(int value);

        [DllImport("libc", EntryPoint = "getenv")]
        public static extern IntPtr Negative([MarshalAs(UnmanagedType.LPStr)] string name);
    }

    internal static class OFR3310
    {
        [ComImport]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface Positive
        {
        }

        public interface Negative
        {
        }
    }

    internal static class OFR3320
    {
        public static int Positive(Exception exception)
        {
            return Marshal.GetHRForException(exception);
        }

        public static int Negative()
        {
            return Marshal.SizeOf(typeof(int));
        }
    }
}
