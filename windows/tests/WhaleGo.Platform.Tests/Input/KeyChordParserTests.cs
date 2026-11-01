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

    [Theory]
    [InlineData("ctrl+s", 0x53, new ushort[] { 0xA2 })]
    [InlineData("Ctrl+Shift+S", 0x53, new ushort[] { 0xA2, 0xA0 })]
    [InlineData("shift+ctrl+s", 0x53, new ushort[] { 0xA2, 0xA0 })]
    [InlineData("alt+f4", 0x73, new ushort[] { 0xA4 })]
    [InlineData("win+r", 0x52, new ushort[] { 0x5B })]
    [InlineData("f5", 0x74, new ushort[0])]
    [InlineData("ctrl+alt+delete", 0x2E, new ushort[] { 0xA2, 0xA4 })]
    public void TryParseHotkey_ReturnsKeyAndCanonicalModifiers(string chord, int expectedVk, ushort[] expectedModifiers)
    {
        Assert.True(KeyChordParser.TryParseHotkey(chord, out var parsed));
        Assert.Equal((ushort)expectedVk, parsed.Key.VirtualKey);
        Assert.Equal(expectedModifiers, parsed.Modifiers.Select(modifier => modifier.VirtualKey).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+")]
    [InlineData("+s")]
    [InlineData("ctrl++s")]
    [InlineData("ctrl+ctrl+s")]
    [InlineData("ctrl+unknown")]
    [InlineData("a+b")]
    [InlineData(null)]
    public void TryParseHotkey_RejectsMalformedChords(string? chord)
    {
        Assert.False(KeyChordParser.TryParseHotkey(chord, out _));
    }
}
