using WhaleGo.Contracts.Model;

namespace WhaleGo.Platform.Uia;

/// <summary>
/// Observation failure channel. <see cref="WhaleGo.Contracts.Abstractions.IObservationEngine.Observe"/> can only return a
/// snapshot (Contracts are frozen), so target-resolution failures are thrown as this typed exception and mapped by the
/// Application/Tool layer to a GoError. Codes come from Contracts.Model.ErrorCodes, currently:
/// WINDOW_NOT_FOUND (missing window/app), AMBIGUOUS_APP (app_id has several windows — see CandidateWindows).
/// </summary>
public sealed class ObservationPlatformException : Exception
{
    public ObservationPlatformException(
        string code,
        string detail,
        IReadOnlyList<WindowInfo>? candidateWindows = null,
        Exception? innerException = null)
        : base($"{code}: {detail}", innerException)
    {
        Code = code;
        Detail = detail;
        CandidateWindows = candidateWindows ?? Array.Empty<WindowInfo>();
    }

    public string Code { get; }

    public string Detail { get; }

    public IReadOnlyList<WindowInfo> CandidateWindows { get; }
}
