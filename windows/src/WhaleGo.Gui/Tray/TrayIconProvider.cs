using System.Drawing;
using System.IO;
using System.Reflection;

namespace WhaleGo.Gui.Tray;

/// <summary>Loads the multi-size .ico variants embedded in WhaleGo.Gui (no runtime GDI+ composition).</summary>
public interface ITrayIconProvider : IDisposable
{
    Icon? Get(TrayState state, TrayIconVariant variant);
}

public sealed class TrayIconProvider : ITrayIconProvider
{
    private readonly Dictionary<(TrayState State, TrayIconVariant Variant), Icon> _cache = [];
    private readonly object _sync = new();
    private bool _disposed;

    public static Assembly ResourcesAssembly => typeof(TrayIconProvider).Assembly;

    public Icon? Get(TrayState state, TrayIconVariant variant)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return null;
            }

            if (_cache.TryGetValue((state, variant), out var cached))
            {
                return cached;
            }

            var icon = Load(state, variant);
            if (icon is not null)
            {
                _cache[(state, variant)] = icon;
            }

            return icon;
        }
    }

    public static Icon? Load(TrayState state, TrayIconVariant variant)
    {
        try
        {
            using var stream = ResourcesAssembly.GetManifestResourceStream(TrayIcons.ResourceName(state, variant));
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return new Icon(buffer);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ListEmbeddedIconResources() =>
        ResourcesAssembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(TrayIcons.ResourcePrefix, StringComparison.Ordinal) &&
                                  name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var icon in _cache.Values)
            {
                icon.Dispose();
            }

            _cache.Clear();
        }
    }
}
