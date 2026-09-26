namespace Legacy.Resources
{
    /// <summary>(g) Generated next to Strings.resx: the pair moves together.</summary>
    internal static class Strings
    {
        private static System.Resources.ResourceManager manager;

        internal static System.Resources.ResourceManager ResourceManager =>
            manager ??= new System.Resources.ResourceManager("Legacy.Resources.Strings", typeof(Strings).Assembly);

        internal static string Greeting => ResourceManager.GetString("Greeting");
    }
}
