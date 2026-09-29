namespace Evidence.Tools
{
    public static class Checksum
    {
        public static int Of(string text)
        {
            return text.Length;
        }

        /// <summary>Only the SDK's users call it.</summary>
        public static int OfBytes(byte[] bytes)
        {
            return bytes.Length;
        }
    }
}
