namespace Foo.Text
{
    /// <summary>ModernF already has a file at Text/Casing.cs: the destination is taken (OFR6003).</summary>
    public static class Casing
    {
        public static string Upper(string value) => value.ToUpperInvariant();
    }
}
