using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhaleGo.Executor.Infrastructure;

/// <summary>Persisted global kill-switch state (%LOCALAPPDATA%\WhaleGo\control-state.json).</summary>
public sealed record ControlStateRecord(bool Stopped, string? Reason, long SinceMs)
{
    public static readonly ControlStateRecord Running = new(false, null, 0);
}

public sealed class ControlStateStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _lock = new();
    private readonly string _path;

    public ControlStateStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public ControlStateRecord Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return ControlStateRecord.Running;
                }

                var text = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<ControlStateRecord>(text, JsonOptions) ?? ControlStateRecord.Running;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return ControlStateRecord.Running;
            }
        }
    }

    public void Save(ControlStateRecord state)
    {
        lock (_lock)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Persistence failure must not crash the kernel; the in-memory stop still holds.
            }
        }
    }
}
