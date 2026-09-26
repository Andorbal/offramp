namespace Legacy
{
    /// <summary>Uses Greeter: once Greeter moves to ModernF (through a project map entry), Legacy needs a reference to it.</summary>
    public static class Welcome
    {
        public static string For(string name) => Greeter.Hello(name) + "!";
    }
}
