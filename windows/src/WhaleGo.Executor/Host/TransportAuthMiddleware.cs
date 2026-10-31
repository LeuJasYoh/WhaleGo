using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Executor.Infrastructure;
using WhaleGo.Executor.Infrastructure.Config;

namespace WhaleGo.Executor.Host;

/// <summary>Fixed-window transport rate limiter keyed by token hash + client IP (HTTP 429 + Retry-After).</summary>
public sealed class TransportRateLimiter
{
    private const int RequestsPerMinute = 600;
    private const long WindowMs = 60_000;
    private readonly object _lock = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private long _lastSweepMs;

    public bool TryAcquire(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            Sweep(now);
            if (!_windows.TryGetValue(key, out var window) || now - window.StartedAtMs >= WindowMs)
            {
                window = new Window(now, 0);
            }

            if (window.Count >= RequestsPerMinute)
            {
                retryAfterSeconds = (int)Math.Clamp(Math.Ceiling((WindowMs - (now - window.StartedAtMs)) / 1000.0), 1, 60);
                _windows[key] = window;
                return false;
            }

            _windows[key] = window with { Count = window.Count + 1 };
            return true;
        }
    }

    private void Sweep(long now)
    {
        if (now - _lastSweepMs < WindowMs)
        {
            return;
        }

        _lastSweepMs = now;
        List<string>? stale = null;
        foreach (var pair in _windows)
        {
            if (now - pair.Value.StartedAtMs >= WindowMs * 2)
            {
                stale ??= [];
                stale.Add(pair.Key);
            }
        }

        if (stale is not null)
        {
            foreach (var key in stale)
            {
                _windows.Remove(key);
            }
        }
    }

    private sealed record Window(long StartedAtMs, int Count);
}

/// <summary>
/// Bearer auth for /mcp (and /healthz when listen.healthzRequireToken): 401 + WWW-Authenticate: Bearer,
/// constant-time SHA-256 comparison inside TokenService, token never logged, HTTP rate limiting after auth.
/// </summary>
public sealed class TransportAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConfigService _config;

    public TransportAuthMiddleware(RequestDelegate next, ConfigService config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context, ITokenService tokens, TransportRateLimiter limiter)
    {
        var path = context.Request.Path;
        var isMcp = path.StartsWithSegments("/mcp");
        var isHealthz = path.StartsWithSegments("/healthz");
        if (!isMcp && !isHealthz)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var requireToken = isMcp || _config.Config.HealthzRequireToken;
        if (requireToken)
        {
            var presented = ReadBearer(context.Request.Headers.Authorization.ToString());
            if (presented is null || !tokens.Verify(presented))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            if (isMcp)
            {
                var rateKey = ClientKey(context, presented);
                if (!limiter.TryAcquire(rateKey, out var retryAfterSeconds))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return;
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    public static string? ReadBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        const string Prefix = "Bearer ";
        return authorization.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[Prefix.Length..].Trim()
            : null;
    }

    private static string ClientKey(HttpContext context, string token)
    {
        var tokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return ip + ":" + tokenHash[..16];
    }
}
