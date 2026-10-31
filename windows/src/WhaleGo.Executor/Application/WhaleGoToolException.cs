using System;
using System.Collections.Generic;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Application;

/// <summary>Application-layer failure that the tool layer turns into an MCP error result (never HTTP 5xx).</summary>
public sealed class WhaleGoToolException : Exception
{
    public WhaleGoToolException(GoError error) : base(error.Message)
    {
        Error = error;
    }

    public GoError Error { get; }

    public static WhaleGoToolException FromCode(
        string code,
        IReadOnlyDictionary<string, object?>? details = null,
        long? retryAfterMs = null,
        string? message = null,
        IReadOnlyDictionary<string, object?>? diagnostic = null) =>
        new(GoErrorFactory.FromCode(code, details: details, diagnostic: diagnostic, retryAfterMs: retryAfterMs, message: message));

    public static WhaleGoToolException FromReceipt(ActionReceipt receipt, string code, string? message = null, long? retryAfterMs = null) =>
        new(GoErrorFactory.FromOutcome(receipt, code, message, retryAfterMs));
}
