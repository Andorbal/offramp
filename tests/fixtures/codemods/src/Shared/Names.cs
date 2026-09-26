namespace Shared
{
    public static class Names
    {
        public static bool IsInternal(string name)
        {
            return name.StartsWith("Internal.");
        }
    }
}
