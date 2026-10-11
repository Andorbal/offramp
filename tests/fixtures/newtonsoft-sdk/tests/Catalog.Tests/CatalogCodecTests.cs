using Contoso.Catalog;
using Xunit;

namespace Contoso.Catalog.Tests
{
    public class CatalogCodecTests
    {
        [Fact]
        public void Round_trips_a_name()
        {
            var json = CatalogCodec.Write(new Product { Id = 1, Name = "Lamp" });

            Assert.Equal("Lamp", CatalogCodec.Read(json).Name);
        }
    }
}
