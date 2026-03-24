using System;
using Avalonia;
using Avalonia.Styling;

namespace ArisenEditorFramework.Services;

public sealed class ThemeManager : IThemeManager
{
    private readonly Application _application;
    private ArisenTheme _currentTheme = ArisenTheme.Dark;

    public ArisenTheme CurrentTheme => _currentTheme;

    public event Action<ArisenTheme>? ThemeChanged;

    public ThemeManager(Application application)
    {
        _application = application;
    }

    public void SetTheme(ArisenTheme theme)
    {
        if (_currentTheme == theme) return;

        _currentTheme = theme;
        ApplyTheme(theme);
        ThemeChanged?.Invoke(theme);
    }

    private void ApplyTheme(ArisenTheme theme)
    {
        _application.RequestedThemeVariant = theme switch
        {
            ArisenTheme.Light => ThemeVariant.Light,
            ArisenTheme.Dark => ThemeVariant.Dark,
            ArisenTheme.HighContrast => ThemeVariant.Dark, // Fallback for now
            _ => ThemeVariant.Dark
        };
    }
}
