using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NetChat.Core;

namespace NetChat.Services;

/// <summary>
/// .nprofile dosyalarını Windows'ta "NetChat Profili" türü olarak kaydeder (kendi simgesiyle);
/// çift tıklanınca NetChat giriş ekranında o profil seçili açılır. Yalnızca geçerli kullanıcı için (HKCU).
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "NetChat.Profile";
    private const string TypeName = "NetChat Profili";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Gömülü simgeyi AppData\NetChat\res içine çıkarır (değiştiyse yeniden yazar). Başarısızsa null.</summary>
    private static string? ExtractIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/profile.ico"));
            if (info == null) return null;
            byte[] data;
            using (var ms = new MemoryStream())
            {
                info.Stream.CopyTo(ms);
                data = ms.ToArray();
            }
            Directory.CreateDirectory(AppPaths.ResourceDir);
            var path = Path.Combine(AppPaths.ResourceDir, "profile.ico");
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
            {
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, path, overwrite: true);
            }
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Profil simgesi çıkarılamadı", ex);
            return null;
        }
    }

    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var icon = ExtractIcon();
            var command = $"\"{exe}\" \"%1\"";
            var iconValue = icon != null ? $"\"{icon}\"" : $"\"{exe}\",0";

            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using (var prog = classes.CreateSubKey(ProgId))
            using (var cmd = classes.CreateSubKey($@"{ProgId}\shell\open\command"))
            using (var defIcon = classes.CreateSubKey($@"{ProgId}\DefaultIcon"))
            using (var ext = classes.CreateSubKey(ProfileFile.Extension))
            {
                // Değişmediyse kabuğu gereksiz yere yenileme.
                if (prog.GetValue(null) as string == TypeName && cmd.GetValue(null) as string == command
                    && defIcon.GetValue(null) as string == iconValue && ext.GetValue(null) as string == ProgId)
                    return;

                prog.SetValue(null, TypeName);
                prog.SetValue("FriendlyTypeName", TypeName);
                cmd.SetValue(null, command);
                defIcon.SetValue(null, iconValue);
                ext.SetValue(null, ProgId);
                ext.SetValue("Content Type", "application/x-netchat-profile");
            }
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Dosya ilişkilendirmesi yapılamadı", ex);
        }
    }
}
