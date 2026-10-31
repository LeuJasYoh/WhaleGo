using System;
using System.Reflection;
using WhaleGo.Contracts.Model;

namespace WhaleGo.Executor.Infrastructure;

/// <summary>
/// Maps platform-layer exceptions (ObservationPlatformException / InputPlatformException in WhaleGo.Platform)
/// to protocol error codes without taking a compile-time dependency on the Platform layer from application code.
/// </summary>
public static class PlatformErrors
{
    public static string? TryGetCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var property = exception.GetType().GetProperty("Code", BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return null;
        }

        object? value;
        try
        {
            value = property.GetValue(exception);
        }
        catch (Exception ex) when (ex is TargetInvocationException or NotSupportedException or MemberAccessException)
        {
            return null;
        }

        return value switch
        {
            null => null,
            string text when text.Length > 0 => Normalize(text),
            Enum enumValue => Normalize(enumValue.ToString()),
            _ => Normalize(value.ToString() ?? string.Empty),
        };
    }

    public static string Normalize(string code)
    {
        var normalized = code.Trim().ToUpperInvariant().Replace('-', '_');
        return normalized switch
        {
            "WINDOW_NOT_FOUND" => ErrorCodes.WindowNotFound,
            "WINDOW_CHANGED" => ErrorCodes.WindowChanged,
            "APP_NOT_FOUND" => ErrorCodes.AppNotFound,
            "AMBIGUOUS_APP" => ErrorCodes.AmbiguousApp,
            "ELEMENT_UNAVAILABLE" => ErrorCodes.ElementUnavailable,
            "ELEMENT_ACTION_UNAVAILABLE" => ErrorCodes.ElementActionUnavailable,
            "STALE_STATE" => ErrorCodes.StaleState,
            "FRAME_NOT_ACTIONABLE" => ErrorCodes.FrameNotActionable,
            "FOREGROUND_REQUIRED" => ErrorCodes.ForegroundRequired,
            "UIPI_BLOCKED" => ErrorCodes.UipiBlocked,
            "ACTION_TIMEOUT" => ErrorCodes.ActionTimeout,
            "LAUNCH_FAILED" => ErrorCodes.LaunchFailed,
            "LAUNCH_TIMEOUT" => ErrorCodes.LaunchTimeout,
            "SESSION_LOCKED" => ErrorCodes.SessionLocked,
            "UIA_UNAVAILABLE" => ErrorCodes.Internal,
            "INTERNAL" => ErrorCodes.Internal,
            _ => normalized,
        };
    }
}
