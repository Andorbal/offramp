namespace Core.Existing
{
    public static class Paths
    {
        public static string For(string title) => "/" + Slug.Of(title);
    }
}
