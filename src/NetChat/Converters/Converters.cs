using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetChat.Controls;
using NetChat.Models;
using Wpf.Ui.Controls;

namespace NetChat.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CountTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i > 99 ? "99+" : i.ToString(culture)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Kimlik/anahtar → avatar arka plan rengi.</summary>
public sealed class KeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        AvatarPalette.BrushFor(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Kimlik → gönderen adı rengi (temaya göre).</summary>
public sealed class KeyToNameBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        AvatarPalette.NameBrushFor(value as string, Services.ThemeService.IsDark);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        MessageStatus.Pending => SymbolRegular.Clock20,
        MessageStatus.Failed => SymbolRegular.ErrorCircle20,
        _ => SymbolRegular.Checkmark20,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TimeTextConverter : IValueConverter
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Sohbet listesi için: bugün → saat, dün → "Dün", bu hafta → gün adı, daha eski → tarih.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset dto || dto == default) return "";
        var local = dto.ToLocalTime();
        var day = local.Date;
        var today = DateTime.Today;
        if (day == today) return local.ToString("HH:mm", culture);
        if (day == today.AddDays(-1)) return "Dün";
        // Arayüz Türkçe: gün adları sistem dilinden bağımsız olarak Türkçe yazılır.
        if ((today - day).TotalDays < 7) return local.ToString("dddd", Turkish);
        return local.ToString("dd.MM.yyyy", Turkish);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Dosya yolundan küçük resim yükler (dosyayı kilitlemeden).</summary>
public sealed class PathToThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 560;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ext = Path.GetExtension(value as string ?? "").ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".jfif" => SymbolRegular.Image24,
            ".pdf" => SymbolRegular.DocumentPdf24,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => SymbolRegular.FolderZip24,
            ".txt" or ".md" or ".log" => SymbolRegular.DocumentText24,
            ".doc" or ".docx" or ".odt" or ".rtf" => SymbolRegular.DocumentText24,
            ".xls" or ".xlsx" or ".csv" => SymbolRegular.DocumentTable24,
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => SymbolRegular.Speaker224,
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => SymbolRegular.ChatVideo24,
            ".exe" or ".msi" => SymbolRegular.WindowApps20,
            _ => SymbolRegular.Document24,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TransferTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not TransferState state) return "";
        var size = values[1] as string ?? "";
        var progress = values[2] is double p ? p : 0;
        var outgoing = values[3] is true;
        return state switch
        {
            TransferState.Transferring => $"{progress:P0} · {size}",
            TransferState.Waiting => outgoing ? $"{size} · Alıcı bekleniyor" : $"{size} · Bekleniyor",
            TransferState.Failed => $"{size} · Aktarılamadı",
            _ => size,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var p = values.Length > 0 && values[0] is double d ? Math.Clamp(d, 0, 1) : 0;
        var w = values.Length > 1 && values[1] is double width ? width : 0;
        return p * w;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush? TrueBrush { get; set; }
    public Brush? FalseBrush { get; set; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Ad → baş harfler (avatar için).</summary>
public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Conversation.MakeInitials(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
