using WhaleGo.Platform.Uia;

namespace WhaleGo.Platform.Input;

/// <summary>
/// Thin adapter between the observation state-handle store (T2, owner of stateId -&gt; UIA handles)
/// and the input layer's <see cref="IElementHandleResolver"/> seam.
/// TryResolve returning false is split into "state expired/unknown" vs "element index gone".
/// </summary>
public sealed class StateHandleStoreResolver : IElementHandleResolver
{
    private readonly StateHandleStore _store;

    public StateHandleStoreResolver(StateHandleStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public ElementResolution Resolve(string stateId, int elementIndex)
    {
        if (string.IsNullOrEmpty(stateId) || elementIndex < 0)
        {
            return ElementResolution.Stale();
        }

        if (_store.TryResolve(stateId, elementIndex, out var element, out _))
        {
            return ElementResolution.Ok(
                element.Element,
                element.WindowId,
                element.WindowPid,
                element.Hwnd,
                element.CapturedAtMs,
                element.Index);
        }

        return _store.GetStatus(stateId) == StateHandleStatus.Live
            ? ElementResolution.Missing()
            : ElementResolution.Stale();
    }
}
