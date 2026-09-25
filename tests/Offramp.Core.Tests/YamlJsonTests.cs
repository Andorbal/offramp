using System.Text.Json;
using Offramp.Core.Configuration;

namespace Offramp.Core.Tests;

public sealed class YamlJsonTests
{
    [Theory]
    [InlineData("a: 1", "{\"a\":1}")]
    [InlineData("a: -3", "{\"a\":-3}")]
    [InlineData("a: 2.0", "{\"a\":2.0}")]
    [InlineData("a: .5", "{\"a\":0.5}")]
    [InlineData("a: 9.0.1", "{\"a\":\"9.0.1\"}")]
    [InlineData("a: true", "{\"a\":true}")]
    [InlineData("a: False", "{\"a\":false}")]
    [InlineData("a: ~", "{\"a\":null}")]
    [InlineData("a:", "{\"a\":null}")]
    [InlineData("a: \"1\"", "{\"a\":\"1\"}")]
    [InlineData("a: 'true'", "{\"a\":\"true\"}")]
    [InlineData("a: yes", "{\"a\":\"yes\"}")]
    [InlineData("a: [x, 2]", "{\"a\":[\"x\",2]}")]
    [InlineData("a: { b: c }", "{\"a\":{\"b\":\"c\"}}")]
    [InlineData("", "{}")]
    public void Scalars_follow_the_yaml_1_2_core_schema(string yaml, string json)
    {
        var document = YamlJson.Parse(yaml);
        Assert.Equal(json, document.Root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    [Fact]
    public void Positions_are_recorded_per_pointer()
    {
        var document = YamlJson.Parse("a:\n  b: 1\n  c:\n    - x\n");

        Assert.Equal(new SourcePosition(2, 6), document.Positions["/a/b"]);
        Assert.Equal(new SourcePosition(2, 3), document.Positions["/a/b#key"]);
        Assert.Equal(new SourcePosition(4, 7), document.Positions["/a/c/0"]);
        Assert.Equal(new SourcePosition(4, 7), document.PositionOf("/a/c/0/missing"));
    }

    [Fact]
    public void Syntax_errors_carry_the_position()
    {
        var ex = Assert.Throws<YamlSyntaxException>(() => YamlJson.Parse("a: [1, 2\nb: 3\n"));
        Assert.True(ex.Position.Line >= 1);
    }
}
