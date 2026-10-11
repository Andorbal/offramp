using Contoso.Inventory;
using NUnit.Framework;

namespace Contoso.Inventory.Tests
{
    [TestFixture]
    public class StockSnapshotTests
    {
        [Test]
        public void Reads_levels()
        {
            var snapshot = StockSnapshotReader.Read("{\"Levels\":{\"lamp\":3}}");

            Assert.That(snapshot.Levels["lamp"], Is.EqualTo(3));
        }
    }
}
