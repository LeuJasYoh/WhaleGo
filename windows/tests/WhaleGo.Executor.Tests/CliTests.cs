using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WhaleGo.Executor.Tests.TestSupport;

namespace WhaleGo.Executor.Tests;

/// <summary>CLI-level checks that do not touch the single-instance mutex (--self-check / --diagnostics-dump).</summary>
public sealed class CliTests
{
    [Fact]
    public async Task SelfCheckJson_CliExitsZero_AndPrintsNoToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "whalego-executor-tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(directory, "data");
        Directory.CreateDirectory(dataDirectory);
        var configPath = Path.Combine(directory, "config.json");

        var result = await RunExecutorAsync(
            ["--self-check", "--json", "--config", configPath],
            dataDirectory,
            TimeSpan.FromSeconds(90));

        Assert.True(result.ExitCode == 0, "stdout: " + result.Stdout + "\nstderr: " + result.Stderr);
        var report = JsonNode.Parse(result.Stdout);
        Assert.NotNull(report);
        Assert.True(report!["ok"]!.GetValue<bool>());
        Assert.Equal(12, report["items"]!.AsArray().Count);

        var secretsPath = Path.Combine(dataDirectory, "secrets.json");
        Assert.True(File.Exists(secretsPath));
        var secrets = JsonNode.Parse(await File.ReadAllTextAsync(secretsPath))!;
        var token = secrets["tokens"]![0]!["token"]!.GetValue<string>();
        Assert.StartsWith("wg_", token);
        Assert.DoesNotContain(token, result.Stdout);
        Assert.DoesNotContain(token, result.Stderr);
        Assert.False(result.Stdout.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnosticsDump_CliExitsZero_WithHealthAndRecentRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "whalego-executor-tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(directory, "data");
        Directory.CreateDirectory(dataDirectory);
        var configPath = Path.Combine(directory, "config.json");

        var result = await RunExecutorAsync(
            ["--diagnostics-dump", "--config", configPath],
            dataDirectory,
            TimeSpan.FromSeconds(90));

        Assert.True(result.ExitCode == 0, "stdout: " + result.Stdout + "\nstderr: " + result.Stderr);
        var dump = JsonNode.Parse(result.Stdout);
        Assert.NotNull(dump);
        Assert.Equal("whalego-health-v1", dump!["health"]!["schema_version"]!.GetValue<string>());
        Assert.Equal(1, dump["health"]!["protocol_version"]!.GetValue<int>());
        Assert.NotNull(dump["recent_requests"]);
        Assert.True(dump["self_check"]!["ok"]!.GetValue<bool>());
    }

    private static async Task<CliResult> RunExecutorAsync(IReadOnlyList<string> arguments, string dataDirectory, TimeSpan timeout)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "WhaleGo.Executor.exe");
        Assert.True(File.Exists(executable), "executor apphost not found: " + executable);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["WHALEGO_DATA_DIR"] = dataDirectory;
        startInfo.Environment["WHALEGO_HEADLESS"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start executor");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cancellation = new System.Threading.CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw new TimeoutException("executor CLI timed out: " + string.Join(' ', arguments));
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}