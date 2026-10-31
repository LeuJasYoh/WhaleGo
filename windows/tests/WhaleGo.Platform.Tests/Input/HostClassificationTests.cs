using FlaUI.Core;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Uia;

namespace WhaleGo.Platform.Tests.Input;

public sealed class HostClassificationTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_1", "chrome", FrameworkType.Unknown, HostClass.Droppable)]
    [InlineData("Chrome_WidgetWin_1", "msedge", FrameworkType.Unknown, HostClass.Droppable)]
    [InlineData("WindowsForms10.Window.8.app.0.1", "MyApp", FrameworkType.WinForms, HostClass.Deliverable)]
    [InlineData("HwndWrapper[MyApp.exe;;guid]", "MyApp", FrameworkType.Wpf, HostClass.Deliverable)]
    [InlineData("Notepad", "notepad", FrameworkType.Win32, HostClass.Deliverable)]
    [InlineData("#32770", "SomeApp", FrameworkType.Win32, HostClass.Deliverable)]
    [InlineData("CustomClass", "someapp", FrameworkType.Unknown, HostClass.Unknown)]
    public void ClassifyHost_MatchesV1Matrix(string className, string processName, FrameworkType framework, HostClass expected)
    {
        Assert.Equal(expected, UiaHitTester.ClassifyHost(className, processName, framework));
    }

    [Fact]
    public void HitTest_WithZeroWindowReturnsNotWithin()
    {
        using var engine = new UiaEngine();
        var hitTester = new UiaHitTester(engine);
        var window = new WindowInfo(
            "w-0000000000000000",
            "app",
            "title",
            "class",
            0,
            false,
            false,
            false,
            false,
            new RectI(0, 0, 10, 10),
            "win32",
            96,
            0);
        var result = hitTester.HitTest(window, 5, 5);
        Assert.False(result.WithinWindow);
        Assert.Equal(HostClass.Unknown, result.HostClass);
    }
}
