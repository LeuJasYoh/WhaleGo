using WhaleGo.Platform.System;
using Xunit.Abstractions;

namespace WhaleGo.Platform.Tests.Input;

[Collection("T3Input")]
public sealed class ThemeHotkeyTests
{
    private readonly InputFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ThemeHotkeyTests(InputFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void WindowTheme_ReturnsBoolWithoutThrowing()
    {
        var theme = new DwmWindowTheme();
        Assert.False(theme.TryApplyDarkTitleBar(IntPtr.Zero, dark: true));

        if (!_fixture.Available)
        {
            _output.WriteLine("SKIP fixture-dependent part: " + (_fixture.SkipReason ?? "unknown reason"));
            return;
        }

        var dark = theme.TryApplyDarkTitleBar(_fixture.Hwnd, dark: true);
        var light = theme.TryApplyDarkTitleBar(_fixture.Hwnd, dark: false);
        _output.WriteLine($"theme hwnd=0x{_fixture.Hwnd:X} dark={dark} light={light}");
        // Win10 < 1809 / Windows without DWM: both false is the documented fallback; no throw is the contract.
    }

    [Fact]
    public void HotkeyRegistrar_RegisterUnregisterCtrlAltShiftF11()
    {
        using var registrar = new HotkeyRegistrar();
        var pressed = 0;
        var registered = registrar.TryRegister("t3-input-test", "Ctrl+Alt+Shift+F11", () => pressed++);
        _output.WriteLine("registered=" + registered);
        Assert.True(registered, "Ctrl+Alt+Shift+F11 should be registrable on an interactive session");
        registrar.Unregister("t3-input-test");
        registrar.Unregister("t3-input-test"); // idempotent
        Assert.Equal(0, pressed);
    }

    [Fact]
    public void HotkeyRegistrar_RejectsInvalidGestures()
    {
        using var registrar = new HotkeyRegistrar();
        Assert.False(registrar.TryRegister("bad-1", "nonsense", () => { }));
        Assert.False(registrar.TryRegister("bad-2", "Ctrl+", () => { }));
        Assert.False(registrar.TryRegister("bad-3", string.Empty, () => { }));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Shift+F12", 0x7Bu)]
    [InlineData("ctrl+shift+f1", 0x70u)]
    [InlineData("Alt+Space", 0x20u)]
    [InlineData("WIN+E", 0x45u)]
    public void HotkeyRegistrar_ParsesGestures(string gesture, uint expectedVk)
    {
        Assert.True(HotkeyRegistrar.TryParseGesture(gesture, out var modifiers, out var virtualKey));
        Assert.Equal(expectedVk, virtualKey);
        Assert.NotEqual(0u, modifiers);
    }
}
