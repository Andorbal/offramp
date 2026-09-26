using System.Web;
using Xunit;

namespace Foo.Web.Tests
{
    /// <summary>Needs System.Web, which Foo references and Foo.Tests does not.</summary>
    public class UrlTests
    {
        [Fact]
        public void Encodes_spaces() => Assert.Equal("a+b", HttpUtility.UrlEncode("a b"));
    }
}
