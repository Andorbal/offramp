namespace Evidence.Formats
{
    public sealed class CsvFormat
    {
        public string Separator
        {
            get { return ","; }
        }

        /// <summary>Nothing in the repository calls it: only tests use this library.</summary>
        public string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        internal static string Unused()
        {
            return "";
        }
    }
}
