using WhaleGo.Platform.Input;

namespace WhaleGo.Platform.Tests.Input;

public sealed class KeyChordParserTests
{
    public static TheoryData<string, int, bool> V1Keys => new()
    {
        { "enter", 0x0D, false },
        { "tab", 0x09, false },
        { "escape", 0x1B, false },
        { "backspace", 0x08, false },
        { "delete", 0x2E, true },
        { "up", 0x26, true },
        { "down", 0x28, true },
        { "left", 0x25, true },
        { "right", 0x27, true },
        { "pageup", 0x21, true },
        { "pagedown", 0x22, true },
        { "home", 0x24, true },
        { "end", 0x23, true },
        { "space", 0x20, false },
    };

    [Theory]
    [MemberData(nameof(V1Keys))]
    public void TryParse_ReturnsVkAndExtendedFlag(string keyName, int expectedVk, bool expectedExtended)
    {
        Assert.True(KeyChordParser.TryParse(keyName, out var stroke));
        Assert.Equal((ushort)expectedVk, stroke.VirtualKey);
        Assert.Equal(expectedExtended, stroke.Extended);
        Assert.NotEqual(0, stroke.ScanCode);
    }

    [Theory]
    [InlineData("ENTER")]
    [InlineData(" Enter ")]
    [InlineData("PageUp")]
    public void TryParse_IsCaseInsensitiveAndTrims(string keyName)
    {
        Assert.True(KeyChordParser.TryParse(keyName, out _));
    }

    [Theory]
    [InlineData("ctrl+c")]
    [InlineData("a+b")]
    [InlineData("shift+enter")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("f1")]
    [InlineData("a")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void TryParse_RejectsChordsAndUnknownKeys(string? keyName)
    {
        Assert.False(KeyChordParser.TryParse(keyName, out _));
    }
}
