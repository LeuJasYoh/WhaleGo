using System;
using System.IO;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure;

namespace WhaleGo.Executor.Tests;

/// <summary>Logs live next to the executable in a normal run; isolated test runs keep them under the data root.</summary>
public sealed class ExecutorPathsTests
{
    [Fact]
    public void NormalRun_PutsLogsNextToExecutable()
    {
        var paths = ExecutorPaths.Resolve(configPath: null, dataDirectoryOverride: null);
        var expected = Path.Combine(AppContext.BaseDirectory, "logs");
        Assert.Equal(expected, paths.LogsRoot);
        Assert.Equal(expected, paths.DefaultAuditDir);
        Assert.Equal("%EXEDIR%\\logs", ConfigDefaults.AuditDir);
    }

    [Fact]
    public void IsolatedRun_KeepsLogsUnderDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "whalego-paths-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = ExecutorPaths.Resolve(configPath: null, dataDirectoryOverride: root);
            Assert.Equal(Path.Combine(root, "logs"), paths.LogsRoot);
            Assert.Equal(Path.Combine(root, "logs"), paths.DefaultAuditDir);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveDirectory_ExpandsExeDirMacro()
    {
        var resolved = ExecutorPaths.ResolveDirectory("%EXEDIR%\\logs", fallback: "fallback", dataRoot: Path.GetTempPath());
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), resolved);
    }
}
