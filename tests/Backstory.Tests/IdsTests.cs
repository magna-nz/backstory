using Backstory.Core;

namespace Backstory.Tests;

public class IdsTests
{
    [Fact]
    public void ContentHash_is_stable_across_calls()
    {
        var a = Ids.ContentHash("telegram", "12345", "hello world");
        var b = Ids.ContentHash("telegram", "12345", "hello world");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ContentHash_is_order_sensitive()
    {
        var a = Ids.ContentHash("a", "b");
        var b = Ids.ContentHash("b", "a");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ContentHash_distinguishes_null_from_empty()
    {
        var withNull = Ids.ContentHash("x", null);
        var withEmpty = Ids.ContentHash("x", "");
        Assert.NotEqual(withNull, withEmpty);
    }

    [Fact]
    public void ContentHash_returns_16_lowercase_hex_chars()
    {
        var id = Ids.ContentHash("anything");
        Assert.Equal(16, id.Length);
        Assert.All(id, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
    }
}
