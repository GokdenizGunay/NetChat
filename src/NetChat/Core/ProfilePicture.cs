using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetChat.Core;

/// <summary>
/// Profil resmi: kare kırpılıp <see cref="Size"/> piksele küçültülmüş JPEG, Base64 olarak saklanır ve ağda taşınır
/// (tipik olarak 10–20 KB).
/// </summary>
public static class ProfilePicture
{
    public const int Size = 160;

    /// <summary>Ağdan gelen resim için üst sınır (Base64 uzunluğu).</summary>
    public const int MaxBase64Length = 200_000;

    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();

    /// <summary>Seçilen dosyayı ortadan kare kırpar, küçültür ve Base64 JPEG'e çevirir. Geçersiz resimde hata fırlatır.</summary>
    public static string FromFile(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();

        BitmapSource source = image;
        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        if (side < 1) throw new InvalidOperationException("Resim boş.");
        var cropped = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        var scale = (double)Size / side;
        var resized = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));

        // Saydam PNG'ler siyah görünmesin: beyaz zemine çiz.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Size, Size));
            dc.DrawImage(resized, new Rect(0, 0, Size, Size));
        }
        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Resmin kısa parmak izi (duyuru paketinde taşınır; değişince karşı taraf yeni resmi ister).</summary>
    public static string? Hash(string? base64) =>
        string.IsNullOrEmpty(base64) ? null : Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(base64)), 0, 8);

    /// <summary>Base64 resmi görüntüye çevirir; geçersizse null. Sonuçlar önbelleğe alınır.</summary>
    public static ImageSource? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > MaxBase64Length) return null;
        if (Cache.TryGetValue(base64, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Size;
            image.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            image.EndInit();
            image.Freeze();
            if (Cache.Count > 500) Cache.Clear();
            Cache[base64] = image;
            return image;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Ağdan gelen resmin geçerli olup olmadığı.</summary>
    public static bool IsValid(string base64) => base64.Length <= MaxBase64Length && Decode(base64) != null;
}
