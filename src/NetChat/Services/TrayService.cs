using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using NetChat.Core;

namespace NetChat.Services;

/// <summary>Bildirim alanı (sistem tepsisi) simgesi. Uygulama kapatıldığında burada çalışmaya devam eder.</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _normal;
    private readonly Icon _badge;
    private readonly ToolStripMenuItem _statusItem;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/netchat.ico"))!.Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        _normal = new Icon(ms, SystemInformation.SmallIconSize);
        _badge = CreateBadgeIcon(_normal);

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        _statusItem = new ToolStripMenuItem(AppPaths.AppName) { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Aç", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Çıkış", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = _normal,
            Text = AppPaths.AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public void SetUnread(int count)
    {
        _icon.Icon = count > 0 ? _badge : _normal;
        var text = count > 0 ? $"{AppPaths.AppName} – {count} okunmamış mesaj" : AppPaths.AppName;
        _icon.Text = text.Length > 63 ? text[..63] : text;
        _statusItem.Text = count > 0 ? $"{count} okunmamış mesaj" : AppPaths.AppName;
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
        _icon.ShowBalloonTip(4000);
    }

    private static Icon CreateBadgeIcon(Icon baseIcon)
    {
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(baseIcon, new Rectangle(Point.Empty, size));
            var d = size.Width * 0.5f;
            var rect = new RectangleF(size.Width - d, 0, d, d);
            using var fill = new SolidBrush(Color.FromArgb(255, 232, 17, 35));
            using var pen = new Pen(Color.White, Math.Max(1f, size.Width / 16f));
            g.FillEllipse(fill, rect);
            g.DrawEllipse(pen, rect);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _badge.Dispose();
        _normal.Dispose();
    }
}
