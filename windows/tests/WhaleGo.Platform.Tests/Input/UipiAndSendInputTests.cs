using System.Text.Json;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Input;
using WhaleGo.Platform.Windows;
using Xunit.Abstractions;

namespace WhaleGo.Platform.Tests.Input;

[Collection("T3Input")]
public sealed class UipiAndSendInputTests
{
    private readonly InputFixture _fixture;
    private readonly ITestOutputHelper _output;

    public UipiAndSendInputTests(InputFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void UipiGuard_SameIntegrityFixture_IsNotBlocked()
    {
        if (!Ready())
        {
            return;
        }

        var guard = new UipiGuard();
        var verdict = guard.Check(_fixture.Pid, out var detail);
        _output.WriteLine($"uipi verdict={verdict} detail={detail}");

        Assert.Equal(UipiVerdict.SameOrLower, verdict);
        Assert.False(UipiGuard.IsBlocked(verdict));
    }

    [Fact]
    public void UipiGuard_OwnAndInvalidProcesses_AreHandledConservatively()
    {
        var guard = new UipiGuard();
        Assert.Equal(UipiVerdict.SameOrLower, guard.Check(Environment.ProcessId));
        Assert.NotEqual(UipiVerdict.SameOrLower, guard.Check(0));

        var bogus = guard.Check(0x7FFFFFF0);
        _output.WriteLine("bogus pid verdict=" + bogus);
        Assert.True(UipiGuard.IsBlocked(bogus));
    }

    [Fact]
    public void SendInput_UnicodeTyping_ReachesFixtureEdit()
    {
        if (!ReadyForPhysicalInput())
        {
            return;
        }

        var text = "T3" + Guid.NewGuid().ToString("N")[..5];
        _fixture.SetInputText(string.Empty);
        if (!FocusEdit())
        {
            _output.WriteLine("SKIP: edit did not receive keyboard focus (another window may hold the foreground)");
            return;
        }

        new SendInputDispatcher { UnicodePacingMs = 5 }.TypeUnicode(text);

        Assert.True(
            TestSupport.WaitUntil(() => _fixture.ReadValue("txtInput").Contains(text, StringComparison.Ordinal), 4000),
            $"value did not get typed text; actual='{_fixture.ReadValue("txtInput")}'");
    }

    [Fact]
    public void SendInput_LeftClick_IncrementsPanelClicks()
    {
        if (!ReadyForPhysicalInput())
        {
            return;
        }

        Assert.True(_fixture.TryActivateFixture(), "fixture window must be activateable for physical input");
        var before = _fixture.ReadClickCounts().Panel;
        var rect = _fixture.Find("pnlClick").BoundingRectangle;
        var centerX = rect.Left + (rect.Width / 2);
        var centerY = rect.Top + (rect.Height / 2);

        new SendInputDispatcher().ClickLeftAt(centerX, centerY);

        Assert.True(
            TestSupport.WaitUntil(() => _fixture.ReadClickCounts().Panel == before + 1, 4000),
            $"panel click count did not increment from {before}");
    }

    [Fact]
    public void ForegroundGuard_TryEnter_ActivatesAndRestoresLease()
    {
        if (!ReadyForPhysicalInput())
        {
            return;
        }

        var guard = new ForegroundGuard(new WindowActivator());
        using var lease = guard.TryEnter(_fixture.Hwnd, 1000);
        Assert.NotNull(lease);
        Assert.True(guard.IsForeground(_fixture.Hwnd));
        _output.WriteLine("lease activated=" + lease!.Activated);
    }

    private bool Ready()
    {
        if (_fixture.Available)
        {
            return true;
        }

        _output.WriteLine("SKIP (cross-process fixture unavailable): " + (_fixture.SkipReason ?? "unknown reason"));
        return false;
    }

    private bool ReadyForPhysicalInput()
    {
        if (!Ready())
        {
            return false;
        }

        if (!TestSupport.IsInteractiveDesktop || TestSupport.GetForegroundWindow() == IntPtr.Zero)
        {
            _output.WriteLine("SKIP (no interactive desktop / foreground window; SendInput tests need the user's session)");
            return false;
        }

        if (!_fixture.TryActivateFixture())
        {
            _output.WriteLine("SKIP (fixture window could not be activated; another app may hold the foreground)");
            return false;
        }

        return true;
    }

    private bool FocusEdit()
    {
        var input = _fixture.Find("txtInput");
        input.Focus();
        var focused = TestSupport.WaitUntil(
            () => _fixture.TryFind("txtInput") is { } current && current.Properties.HasKeyboardFocus.ValueOrDefault,
            1500);
        _output.WriteLine($"focused edit={focused} foreground={_fixture.IsFixtureForeground()}");
        return focused;
    }
}