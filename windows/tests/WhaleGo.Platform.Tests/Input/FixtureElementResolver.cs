using FlaUI.Core.AutomationElements;
using WhaleGo.Platform.Uia;

namespace WhaleGo.Platform.Tests.Input;

/// <summary>Deterministic test resolver: (stateId, index) -> live fixture UIA element (no observation engine needed).</summary>
internal sealed class FixtureElementResolver : IElementHandleResolver
{
    public const string StateId = "st-0000abcd";

    public static readonly IReadOnlyDictionary<int, string> DefaultIds = new Dictionary<int, string>
    {
        [0] = "btnIncrement",
        [1] = "lblCounter",
        [2] = "txtInput",
        [3] = "chkToggle",
        [4] = "lstItems",
        [5] = "pnlClick",
        [6] = "lblClickCount",
        [7] = "txtSecret",
        [8] = "btnDisabled",
        [9] = "btnSetText",
    };

    private readonly InputFixture _fixture;
    private readonly IReadOnlyDictionary<int, string> _ids;

    public FixtureElementResolver(InputFixture fixture, IReadOnlyDictionary<int, string>? ids = null)
    {
        _fixture = fixture;
        _ids = ids ?? DefaultIds;
    }

    public ElementResolution Resolve(string stateId, int elementIndex)
    {
        if (!_fixture.Available || !string.Equals(stateId, StateId, StringComparison.Ordinal))
        {
            return ElementResolution.Stale();
        }

        if (!_ids.TryGetValue(elementIndex, out var automationId))
        {
            return ElementResolution.Missing();
        }

        AutomationElement? element;
        try
        {
            element = _fixture.TryFind(automationId);
        }
        catch (Exception)
        {
            element = null;
        }

        if (element is null)
        {
            return ElementResolution.Missing();
        }

        return ElementResolution.Ok(
            element,
            _fixture.WindowId,
            _fixture.Pid,
            _fixture.Hwnd,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            elementIndex);
    }
}