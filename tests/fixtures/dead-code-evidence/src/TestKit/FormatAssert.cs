using Evidence.Formats;
using NUnit.Framework;

namespace Evidence.TestKit
{
    public static class FormatAssert
    {
        public static void UsesCommas(CsvFormat format)
        {
            Assert.AreEqual(",", format.Separator);
        }
    }
}
