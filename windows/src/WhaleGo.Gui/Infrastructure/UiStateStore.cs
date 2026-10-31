using System.IO;
using System.Text.Json;

namespace WhaleGo.Gui.Infrastructure;

/// <summary>Pure UI state (window size + last page). Kept out of business config, per docs/GUI.md section 10.7.</summary>
public sealed record UiState(int LastPageIndex, double Width, double Height)
{
    public static readonly UiState Default = new(0, 880, 620);
}

public interface IUiStateStore
{
    UiState Load();

    void Save(UiState state);
}

/// <summary>
/// %LOCALAPPDATA%\WhaleGo\ui-state.json. All IO failures degrade to defaults: UI state must never break startup.
/// </summary>
public sealed class FileUiStateStore : IUiStateStore
{
    private readonly string? _path;

    public FileUiStateStore(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "ui-state.json");
        }
        catch
        {
            _path = null;
        }
    }

    public UiState Load()
    {
        if (_path is null)
        {
            return UiState.Default;
        }

        try
        {
            if (!File.Exists(_path))
            {
                return UiState.Default;
            }

            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<UiState>(json);
            return state ?? UiState.Default;
        }
        catch
        {
            return UiState.Default;
        }
    }

    public void Save(UiState state)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // best effort only
        }
    }
}
