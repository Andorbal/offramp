using Evidence.Formats;
using Evidence.TestKit;

namespace Evidence.Specs
{
    /// <summary>Used by nothing, in a library nothing references.</summary>
    public class FormatSpecs
    {
        public void Separator_is_a_comma()
        {
            FormatAssert.UsesCommas(new CsvFormat());
        }
    }
}
