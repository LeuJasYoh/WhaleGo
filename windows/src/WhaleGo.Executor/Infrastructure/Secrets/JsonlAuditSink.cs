using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhaleGo.Contracts.Abstractions;
using WhaleGo.Contracts.Domain;
using WhaleGo.Contracts.Model;
using WhaleGo.Executor.Infrastructure.Config;

namespace WhaleGo.Executor.Infrastructure.Secrets;

/// <summary>JSONL audit sink: append lines, flush to disk before returning (action records are fail-closed).</summary>
public sealed class JsonlAuditSink : IAuditSink
{
    private readonly ConfigService _config;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlAuditSink(ConfigService config)
    {
        _config = config;
    }

    public string AuditDirectory => _config.EffectiveAuditDir;

    public string CurrentAuditFile => Path.Combine(AuditDirectory, "audit-" + DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");

    public async ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (!_config.Config.AuditEnabled)
        {
            return;
        }

        var line = WireJson.Serialize(auditEvent) + "\n";
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(AuditDirectory);
            await using var stream = new FileStream(
                CurrentAuditFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            var bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Fail-closed preflight: prove the audit directory/file is writable and durable before dispatching.</summary>
    public async ValueTask EnsureWritableAsync(CancellationToken cancellationToken)
    {
        if (!_config.Config.AuditEnabled)
        {
            return;
        }

        Directory.CreateDirectory(AuditDirectory);
        await using var stream = new FileStream(
            CurrentAuditFile,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 256,
            useAsync: true);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Read the newest audit file's tail without blocking the writer (tolerates concurrent writers).</summary>
    public IReadOnlyList<string> ReadTail(int lines)
    {
        var take = Math.Clamp(lines, 1, 2000);
        try
        {
            Directory.CreateDirectory(AuditDirectory);
            var files = Directory.GetFiles(AuditDirectory, "audit-*.jsonl");
            if (files.Length == 0)
            {
                return [];
            }

            Array.Sort(files, StringComparer.Ordinal);
            var newest = files[^1];
            var buffer = new List<string>(take);
            using var stream = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var queue = new Queue<string>(take);
            while (reader.ReadLine() is { } line)
            {
                if (queue.Count == take)
                {
                    queue.Dequeue();
                }
                queue.Enqueue(line.Length > 4096 ? line[..4096] : line);
            }
            buffer.AddRange(queue);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
