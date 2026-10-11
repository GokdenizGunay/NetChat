using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetChat.Controls;

/// <summary>Kimliğe göre tutarlı avatar renkleri ve bildirimler için PNG avatar üretimi.</summary>
public static class AvatarPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(0xE1, 0x4D, 0x5A), Color.FromRgb(0xF2, 0x8C, 0x28), Color.FromRgb(0xC9, 0x9A, 0x06),
        Color.FromRgb(0x2E, 0xA0, 0x4F), Color.FromRgb(0x0E, 0x9F, 0x9A), Color.FromRgb(0x1F, 0x88, 0xE5),
        Color.FromRgb(0x5B, 0x5F, 0xE0), Color.FromRgb(0x8E, 0x4D, 0xD8), Color.FromRgb(0xD1, 0x4B, 0xA6),
        Color.FromRgb(0x6D, 0x83, 0x99),
    ];

    public static Color ColorFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return Colors[^1];
        uint hash = 2166136261;
        foreach (var c in key) hash = (hash ^ c) * 16777619;
        return Colors[hash % (uint)Colors.Length];
    }

    public static SolidColorBrush BrushFor(string? key)
    {
        var brush = new SolidColorBrush(ColorFor(key));
        brush.Freeze();
        return brush;
    }

    /// <summary>Gönderen adları için okunaklı (biraz daha açık) renk.</summary>
    public static SolidColorBrush NameBrushFor(string? key, bool dark)
    {
        var c = ColorFor(key);
        if (dark)
            c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.35), (byte)(c.G + (255 - c.G) * 0.35), (byte)(c.B + (255 - c.B) * 0.35));
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>Baş harfli dairesel avatarı PNG olarak kaydeder (toast bildirimleri için).</summary>
    public static string? RenderPng(string key, string initials, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var safe = string.Concat(key.Where(char.IsLetterOrDigit));
            var path = Path.Combine(folder, $"{safe}-{string.Concat(initials.Where(char.IsLetterOrDigit))}.png");
            if (File.Exists(path)) return path;

            const int size = 96;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawEllipse(BrushFor(key), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
                var text = new FormattedText(initials, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    38, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
