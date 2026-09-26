using Microsoft.Win32;

namespace Legacy.Platform
{
    /// <summary>Compiles on modern .NET, but the registry exists only on Windows.</summary>
    public static class RegistryReader
    {
        public static string ProductName() =>
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")?.GetValue("ProductName") as string ?? "";
    }
}
