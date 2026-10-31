namespace WhaleGo.Contracts.Model;

/// <summary>One JSONL audit record. Action records are durable before the tool returns; observe records may be async.</summary>
public sealed record AuditEvent(
    long TsMs,
    string Kind,
    string? SessionId = null,
    string? Tool = null,
    string? Op = null,
    string? WindowId = null,
    string? AppId = null,
    string? TargetKind = null,
    bool? ActionSent = null,
    DispatchStatus? DispatchStatus = null,
    string? Method = null,
    bool? Promoted = null,
    string? Verification = null,
    long? ElapsedMs = null,
    string? ErrorCode = null,
    string? Event = null,
    string? Reason = null,
    string? Actor = null,
    int? TextLength = null,
    string? TextSha256Prefix = null,
    int? ImageBytes = null)
{
    public const string KindAction = "action";
    public const string KindObserve = "observe";
    public const string KindControl = "control";
}
