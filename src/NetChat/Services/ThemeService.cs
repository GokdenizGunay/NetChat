using System.Windows;
using Microsoft.Win32;
using NetChat.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NetChat.Services;

/// <summary>Açık/koyu/sistem teması ve Windows 11'de Mica arka planı.</summary>
public static class ThemeService
{
    public static bool IsWindows11 { get; } = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Mica yalnızca koyu temada: açık temada Mica neredeyse saf beyaz görünür, soluk tonlarımız kullanılır.</summary>
    public static WindowBackdropType Backdrop => IsWindows11 && IsDark ? WindowBackdropType.Mica : WindowBackdropType.None;

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    private static AppThemeMode _mode;
    private static bool _watching;

    public static void Apply(AppThemeMode mode)
    {
        _mode = mode;
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (_mode == AppThemeMode.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppThemeMode.System));
            };
        }

        var theme = mode switch
        {
            AppThemeMode.Light => ApplicationTheme.Light,
            AppThemeMode.Dark => ApplicationTheme.Dark,
            _ => SystemPrefersDark() ? ApplicationTheme.Dark : ApplicationTheme.Light,
        };
        IsDark = theme == ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(theme, Backdrop, true);
        ApplySoftLightPalette(!IsDark);
        // Giden balonlar vurgu renginde: koyu temada vurgu açık, açık temada koyu olduğu için
        // "okundu" tikleri her iki durumda da ayırt edilebilir bir renk almalı.
        Application.Current.Resources["ReadTickBrush"] = new System.Windows.Media.SolidColorBrush(
            // Yeşil okundu tiki: koyu temada açık mavi balon üzerinde koyu yeşil, açık temada koyu mavi balon üzerinde parlak yeşil.
            IsDark ? System.Windows.Media.Color.FromRgb(0x05, 0x6B, 0x2E) : System.Windows.Media.Color.FromRgb(0x5B, 0xF0, 0x8E));
        // Mesajlardaki bağlantı renkleri: balon arka planına göre okunaklı maviler.
        var res = Application.Current.Resources;
        res["LinkIncomingBrush"] = Brush(IsDark ? 0x6CB8FF : 0x0A5FC4);
        res["LinkOutgoingBrush"] = Brush(IsDark ? 0x0B3D91 : 0xC9E6FF);
        foreach (Window window in Application.Current.Windows)
            UpdateWindow(window);
        Changed?.Invoke();
    }

    /// <summary>
    /// Açık tema için göz yormayan, soluk gri-beyaz tonlar. Uygulama kaynaklarına yazılan fırçalar
    /// temanın kendi fırçalarından önce bulunur; koyu temaya dönüldüğünde kaldırılır.
    /// </summary>
    private static readonly (string Key, int Rgb)[] SoftLight =
    [
        ("ApplicationBackgroundBrush", 0xE6E9EE),
        ("SolidBackgroundFillColorBaseBrush", 0xE3E6EB),
        ("SolidBackgroundFillColorSecondaryBrush", 0xDCDFE5),
        ("SolidBackgroundFillColorTertiaryBrush", 0xEDEFF2),
        ("LayerFillColorDefaultBrush", 0xECEEF2),
        ("LayerFillColorAltBrush", 0xE8EBEF),
        ("CardBackgroundFillColorDefaultBrush", 0xF3F4F6),
        ("CardBackgroundFillColorSecondaryBrush", 0xEDEFF2),
        ("ControlFillColorDefaultBrush", 0xF4F5F7),
        ("ControlFillColorSecondaryBrush", 0xEEF0F3),
        ("ControlFillColorInputActiveBrush", 0xF7F8FA),
    ];

    private static void ApplySoftLightPalette(bool enable)
    {
        var res = Application.Current.Resources;
        foreach (var (key, rgb) in SoftLight)
        {
            if (enable) res[key] = Brush(rgb);
            else res.Remove(key);
        }
    }

    private static System.Windows.Media.SolidColorBrush Brush(int rgb)
    {
        var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    public static void UpdateWindow(Window window)
    {
        var theme = IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        if (window is FluentWindow fluent) fluent.WindowBackdropType = Backdrop;
        window.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        WindowBackgroundManager.UpdateBackground(window, theme, Backdrop);
        if (Backdrop == WindowBackdropType.None)
        {
            // Windows 10 ya da açık tema: Mica yok, düz (soluk) tema arka planı kullan.
            window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
        }
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.HCBlack;
        }
    }
}
