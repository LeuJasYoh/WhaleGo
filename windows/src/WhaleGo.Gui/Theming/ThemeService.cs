using WhaleGo.Contracts.Abstractions;
using WhaleGo.Gui.Infrastructure;

namespace WhaleGo.Gui.Theming;

/// <summary>
/// Owns the visual theme decision and the hot swap: preference (ui.theme) + OS light/dark + high contrast,
/// dictionary swap, title-bar notification, failure rollback. Contains no WPF view code so it is unit-testable.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly IThemeResourceHost _resources;
    private readonly ISystemThemeProvider _systemTheme;
    private readonly IWindowTheme _windowTheme;
    private readonly IAppLogger _logger;
    private readonly List<IntPtr> _titleBarHandles = [];
    private readonly object _sync = new();
    private bool _initialized;
    private bool _disposed;
    private ThemePreference _preference = ThemePreference.System;
    private bool _loggedTitleBarFallback;

    public ThemeService(
        IThemeResourceHost resources,
        ISystemThemeProvider systemTheme,
        IWindowTheme windowTheme,
        IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(systemTheme);
        ArgumentNullException.ThrowIfNull(windowTheme);
        ArgumentNullException.ThrowIfNull(logger);
        _resources = resources;
        _systemTheme = systemTheme;
        _windowTheme = windowTheme;
        _logger = logger;
        _systemTheme.Changed += OnSystemThemeChanged;
    }

    public ThemePreference Preference => _preference;

    /// <summary>High contrast always wins; otherwise the preference resolves against the OS setting.</summary>
    public AppTheme EffectiveTheme => _systemTheme.IsHighContrast
        ? AppTheme.HighContrast
        : ThemePreferences.Resolve(_preference, _systemTheme.IsSystemDarkMode);

    /// <summary>Win11 DWM immersive dark title bar; high contrast keeps the native title bar untouched.</summary>
    public bool IsDarkTitleBar => EffectiveTheme == AppTheme.Dark;

    public event EventHandler<AppTheme>? ThemeChanged;

    public bool Initialize(ThemePreference preference)
    {
        _preference = preference;
        var applied = TryApply(EffectiveTheme);
        _initialized = true;
        return applied;
    }

    /// <summary>
    /// Applies a new preference. Returns false and rolls the visual state back to the previous theme when the
    /// dictionary cannot be applied; persistence + persistence-failure rollback is the caller's job.
    /// </summary>
    public bool SetPreference(ThemePreference preference)
    {
        var previous = _preference;
        _preference = preference;
        if (TryApply(EffectiveTheme))
        {
            return true;
        }

        _preference = previous;
        TryApply(EffectiveTheme);
        return false;
    }

    /// <summary>Re-evaluates the effective theme (used after a manual "follow system" refresh).</summary>
    public void RefreshFromSystem() => TryApply(EffectiveTheme);

    public void RegisterTitleBarHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (_sync)
        {
            if (!_titleBarHandles.Contains(hwnd))
            {
                _titleBarHandles.Add(hwnd);
            }
        }

        ApplyTitleBar(hwnd);
    }

    public void UnregisterTitleBarHandle(IntPtr hwnd)
    {
        lock (_sync)
        {
            _titleBarHandles.Remove(hwnd);
        }
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        TryApply(EffectiveTheme);
    }

    private bool TryApply(AppTheme theme)
    {
        var previous = _resources.Current;
        try
        {
            _resources.Apply(theme);
        }
        catch (Exception ex)
        {
            _logger.Error("theme apply failed for " + theme, ex);
            if (previous != theme)
            {
                try
                {
                    _resources.Apply(previous);
                }
                catch (Exception rollbackException)
                {
                    _logger.Error("theme rollback failed", rollbackException);
                }
            }

            return false;
        }

        List<IntPtr> handles;
        lock (_sync)
        {
            handles = [.. _titleBarHandles];
        }

        foreach (var hwnd in handles)
        {
            ApplyTitleBar(hwnd);
        }

        if (_initialized)
        {
            ThemeChanged?.Invoke(this, theme);
        }

        return true;
    }

    private void ApplyTitleBar(IntPtr hwnd)
    {
        try
        {
            var dark = IsDarkTitleBar;
            if (!_windowTheme.TryApplyDarkTitleBar(hwnd, dark) && dark && !_loggedTitleBarFallback)
            {
                _loggedTitleBarFallback = true;
                _logger.Warn("dark title bar not supported on this Windows build; falling back to a light title bar");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("title bar theme update failed", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _systemTheme.Changed -= OnSystemThemeChanged;
        lock (_sync)
        {
            _titleBarHandles.Clear();
        }
    }
}
