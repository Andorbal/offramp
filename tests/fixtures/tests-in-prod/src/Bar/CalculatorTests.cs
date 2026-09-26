using NUnit.Framework;

namespace Bar
{
    [TestFixture]
    public class CalculatorTests
    {
        [Test]
        public void Adds() => Assert.That(Calculator.Add(2, 3), Is.EqualTo(5));
    }
}
