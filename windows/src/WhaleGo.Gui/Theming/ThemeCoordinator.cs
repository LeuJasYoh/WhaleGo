using System.Threading.Tasks;
using WhaleGo.Contracts.Model;
using WhaleGo.Gui.Infrastructure;

namespace WhaleGo.Gui.Theming;

/// <summary>
/// Presentation-level theme operations: ui.theme is special (docs/GUI.md 6.4) — switching applies immediately and
/// persists right away; if persistence fails the visual theme is rolled back to the previous value.
/// </summary>
public interface IThemeController
{
    ThemePreference Preference { get; }

    AppTheme EffectiveTheme { get; }

    event EventHandler<AppTheme>? ThemeChanged;

    Task<bool> SetPreferenceAsync(ThemePreference preference);
}

public sealed class ThemeCoordinator : IThemeController, IDisposable
{
    private readonly ThemeService _themeService;
    private readonly SafeControl _safeControl;
    private readonly IAppLogger _logger;

    public ThemeCoordinator(ThemeService themeService, SafeControl safeControl, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(safeControl);
        ArgumentNullException.ThrowIfNull(logger);
        _themeService = themeService;
        _safeControl = safeControl;
        _logger = logger;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    public ThemePreference Preference => _themeService.Preference;

    public AppTheme EffectiveTheme => _themeService.EffectiveTheme;

    public event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>Applies + persists ui.theme. Returns false when either step failed (theme rolled back).</summary>
    public async Task<bool> SetPreferenceAsync(ThemePreference preference)
    {
        var previous = _themeService.Preference;
        if (!_themeService.SetPreference(preference))
        {
            _logger.Warn("theme switch failed: " + preference);
            return false;
        }

        var patch = new ConfigPatch(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ConfigKeys.UiTheme] = preference.ToConfigValue(),
        });
        var result = await _safeControl.SetConfigAsync(patch).ConfigureAwait(true);
        if (result.Success)
        {
            return true;
        }

        _themeService.SetPreference(previous);
        _logger.Warn("theme persistence failed, reverted to " + previous);
        return false;
    }

    private void OnThemeChanged(object? sender, AppTheme theme) => ThemeChanged?.Invoke(this, theme);

    public void Dispose() => _themeService.ThemeChanged -= OnThemeChanged;
}
