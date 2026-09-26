namespace Foo.Formatting
{
    /// <summary>Two types in one file; the file moves whole.</summary>
    public enum NameStyle
    {
        Given,
        Family,
    }

    public static class Names
    {
        public static string Format(string given, string family, NameStyle style) =>
            style == NameStyle.Given ? given + " " + family : family + ", " + given;
    }
}
