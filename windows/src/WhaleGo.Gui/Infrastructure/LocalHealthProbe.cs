using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace WhaleGo.Gui.Infrastructure;

public sealed record LocalHealthProbeResult(
    bool Ok,
    long ElapsedMs,
    int? UnauthenticatedStatus,
    int? AuthenticatedStatus,
    string Detail);

/// <summary>
/// Loopback connection test for the pairing wizard / connection page (docs/GUI.md 7.4): GET /healthz with and
/// without the bearer token. This is a local UI diagnostic only — it never goes through the kernel and never
/// exposes a new HTTP route.
/// </summary>
public sealed class LocalHealthProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly IAppLogger _logger;
    private readonly TimeSpan _timeout;

    public LocalHealthProbe(IAppLogger logger, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<LocalHealthProbeResult> ProbeAsync(int port, string? token, bool tokenRequired)
    {
        var uri = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/healthz";
        using var client = new HttpClient { Timeout = _timeout };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var unauthenticated = await client.GetAsync(uri).ConfigureAwait(false);
            var unauthStatus = (int)unauthenticated.StatusCode;
            int? authStatus = null;
            if (!string.IsNullOrEmpty(token))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var authenticated = await client.SendAsync(request).ConfigureAwait(false);
                authStatus = (int)authenticated.StatusCode;
            }

            stopwatch.Stop();
            var ok = tokenRequired
                ? unauthStatus == 401 && (authStatus is null || authStatus == 200)
                : unauthStatus == 200 && (authStatus is null || authStatus == 200);
            var detail = BuildDetail(ok, unauthStatus, authStatus, tokenRequired, token is not null, stopwatch.ElapsedMilliseconds);
            return new LocalHealthProbeResult(ok, stopwatch.ElapsedMilliseconds, unauthStatus, authStatus, detail);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Warn("local health probe failed: " + ex.Message);
            return new LocalHealthProbeResult(
                false,
                stopwatch.ElapsedMilliseconds,
                null,
                null,
                "连接失败：" + ex.Message + "（执行器未启动 / 端口未监听 / 本机回环被拦截）");
        }
    }

    private static string BuildDetail(bool ok, int unauthStatus, int? authStatus, bool tokenRequired, bool hasToken, long elapsedMs)
    {
        var authText = authStatus is null ? "未测（token 不可读）" : authStatus.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = ok ? "连接正常" : "连接异常";
        var expectation = tokenRequired
            ? "（healthzRequireToken=true：无 token 应 401，带 token 应 200）"
            : "（healthzRequireToken=false：无 token 应 200）";
        return prefix + "：" + elapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms · 无 token=" +
               unauthStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · 带 token=" + authText +
               (hasToken ? string.Empty : " · token 不可读无法做认证测试") + expectation;
    }
}
