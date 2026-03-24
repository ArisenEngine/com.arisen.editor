using System;

namespace ArisenEditorFramework.Services;

public enum ArisenTheme
{
    Light,
    Dark,
    HighContrast
}

public interface IThemeManager
{
    ArisenTheme CurrentTheme { get; }
    void SetTheme(ArisenTheme theme);
    event Action<ArisenTheme>? ThemeChanged;
}
