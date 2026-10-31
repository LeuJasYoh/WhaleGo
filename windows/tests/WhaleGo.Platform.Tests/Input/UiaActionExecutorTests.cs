using System.Text.Json;
using WhaleGo.Contracts.Model;
using WhaleGo.Platform.Input;
using WhaleGo.Platform.Uia;
using WhaleGo.Platform.Windows;
using Xunit.Abstractions;

namespace WhaleGo.Platform.Tests.Input;

[Collection("T3Input")]
public sealed class UiaActionExecutorTests
{
    private readonly InputFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly UiaActionExecutor _executor;

    public UiaActionExecutorTests(InputFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _executor = new UiaActionExecutor(
            new FixtureElementResolver(fixture),
            new UipiGuard(),
            new ForegroundGuard(new WindowActivator()),
            new SendInputDispatcher { UnicodePacingMs = 5 },
            new PostMessageDispatcher(),
            new InputLane());
    }

    private const string State = FixtureElementResolver.StateId;

    [Fact]
    public async Task Click_InvokePattern_IncrementsCounter()
    {
        if (!Ready())
        {
            return;
        }

        var before = _fixture.ReadCounter();
        var outcome = await _executor.ExecuteAsync(State, 0, Plan("click"), CancellationToken.None);
        _output.WriteLine("outcome: " + JsonSerializer.Serialize(outcome));

        Assert.True(outcome.Sent, "click must be sent");
        Assert.False(outcome.Uncertain);
        Assert.Equal("uia", outcome.Method);
        Assert.False(outcome.Escalated);
        Assert.True(
            TestSupport.WaitUntil(() => _fixture.ReadCounter() == before + 1, 4000),
            $"counter did not increment (before={before}, now={_fixture.ReadCounter()})");
    }

    [Fact]
    public async Task SetValue_ValuePattern_ReadsBackVerified()
    {
        if (!Ready())
        {
            return;
        }

        var value = "wg-" + Guid.NewGuid().ToString("N")[..8];
        var outcome = await _executor.ExecuteAsync(State, 2, Plan("set_value", setValue: value), CancellationToken.None);
        _output.WriteLine("outcome: " + JsonSerializer.Serialize(outcome));

        Assert.True(outcome.Sent);
        Assert.Equal("uia", outcome.Method);
        Assert.Equal("verified", outcome.VerificationStatus);
        Assert.Equal(value, _fixture.ReadValue("txtInput"));
    }

    [Fact]
    public async Task Type_ValuePattern_ReadBackContainsText()
    {
        if (!Ready())
        {
            return;
        }

        _fixture.SetInputText("base-");
        var text = "typed" + Guid.NewGuid().ToString("N")[..4];
        var outcome = await _executor.ExecuteAsync(State, 2, Plan("type", typeText: text), CancellationToken.None);
        _output.WriteLine("outcome: " + JsonSerializer.Serialize(outcome));

        Assert.True(outcome.Sent);
        Assert.Equal("verified", outcome.VerificationStatus);
        var readback = _fixture.ReadValue("txtInput");
        Assert.Contains(text, readback, StringComparison.Ordinal);
        Assert.StartsWith("base-", readback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_OnEdit_IsAcceptedWithoutCrash()
    {
        if (!Ready())
        {
            return;
        }

        var outcome = await _executor.ExecuteAsync(State, 2, Plan("key", key: "space"), CancellationToken.None);
        _output.WriteLine("outcome: " + JsonSerializer.Serialize(outcome));

        Assert.NotNull(outcome);
        if (outcome.Sent)
        {
            Assert.False(outcome.Uncertain);
            Assert.Equal("post_message", outcome.Method);
        }
        else
        {
            // Only acceptable not-sent path is "cannot safely activate" (headless/locked desktop).
            Assert.Contains(outcome.FailureCode, new[] { ErrorCodes.ForegroundRequired, ErrorCodes.ElementUnavailable });
        }
    }

    [Fact]
    public async Task Scroll_OnList_DoesNotThrowAndReportsMethod()
    {
        if (!Ready())
        {
            return;
        }

        var outcome = await _executor.ExecuteAsync(State, 4, Plan("scroll", scrollDirection: "down", scrollPages: 1), CancellationToken.None);
        _output.WriteLine("outcome: " + JsonSerializer.Serialize(outcome));

        Assert.NotNull(outcome);
        if (outcome.Sent)
        {
            Assert.Contains(outcome.Method, new[] { "uia", "send_input" });
        }
        else
        {
            Assert.Contains(outcome.FailureCode, new[] { ErrorCodes.ForegroundRequired, ErrorCodes.ElementActionUnavailable, ErrorCodes.ElementUnavailable });
        }
    }

    [Fact]
    public async Task DisabledButton_IsNotSent()
    {
        if (!Ready())
        {
            return;
        }

        var outcome = await _executor.ExecuteAsync(State, 8, Plan("click"), CancellationToken.None);
        _output.WriteLine("outcome: " + JsonSerializer.Serialize(outcome));

        Assert.False(outcome.Sent);
        Assert.False(outcome.Uncertain);
        Assert.Equal(ErrorCodes.ElementUnavailable, outcome.FailureCode);
    }

    [Fact]
    public async Task UnknownState_IsNotSentWithStaleState()
    {
        if (!Ready())
        {
            return;
        }

        var outcome = await _executor.ExecuteAsync("st-deadbeef", 0, Plan("click"), CancellationToken.None);
        Assert.False(outcome.Sent);
        Assert.Equal(ErrorCodes.StaleState, outcome.FailureCode);
    }

    [Fact]
    public async Task MissingElementIndex_IsNotSentWithElementUnavailable()
    {
        if (!Ready())
        {
            return;
        }

        var outcome = await _executor.ExecuteAsync(State, 123, Plan("click"), CancellationToken.None);
        Assert.False(outcome.Sent);
        Assert.Equal(ErrorCodes.ElementUnavailable, outcome.FailureCode);
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

    private static ActionPlan Plan(
        string op,
        string? setValue = null,
        string? typeText = null,
        string? key = null,
        string? scrollDirection = null,
        double scrollPages = 1) =>
        new(
            op,
            TargetKind.Element,
            ElementIndex: 0,
            StateId: State,
            SetValue: setValue,
            TypeText: typeText,
            Key: key,
            ScrollDirection: scrollDirection,
            ScrollPages: scrollPages);
}
