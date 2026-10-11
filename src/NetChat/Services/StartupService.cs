using Microsoft.Win32;
using NetChat.Core;

namespace NetChat.Services;

/// <summary>Windows oturumu açıldığında uygulamayı arka planda başlatma (HKCU\...\Run).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName => AppPaths.Profile == "default" ? AppPaths.AppId : $"{AppPaths.AppId}-{AppPaths.Profile}";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                var args = "--minimized" + (AppPaths.Profile == "default" ? "" : $" --profile {AppPaths.Profile}");
                key.SetValue(ValueName, $"\"{exe}\" {args}");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Başlangıç ayarı uygulanamadı", ex);
        }
    }
}
