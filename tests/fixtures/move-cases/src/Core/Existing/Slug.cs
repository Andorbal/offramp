namespace Core.Existing
{
    /// <summary>Used by Paths, which stays in Core when Slug is asked to move up into Reports.</summary>
    public static class Slug
    {
        public static string Of(string text) => text.Trim().ToLowerInvariant().Replace(' ', '-');
    }
}
