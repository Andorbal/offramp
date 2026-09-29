using System.ComponentModel;
using Evidence.Formats;
using Evidence.TestKit;
using NUnit.Framework;

namespace Evidence.Specs
{
    /// <summary>No [TestFixture]: NUnit 2.5 and later find the class by its [Test] methods.</summary>
    public class FormatSpecs
    {
        [Test]
        public void Separator_is_a_comma()
        {
            FormatAssert.UsesCommas(new CsvFormat());
        }
    }

    /// <summary>An attribute on a method that is not a test framework's: nothing finds the class by it.</summary>
    public class SpecNotes
    {
        [Description("notes")]
        public void Write()
        {
            System.Console.WriteLine("notes");
        }
    }
}
