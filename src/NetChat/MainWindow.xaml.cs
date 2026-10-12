using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetChat.Controls;
using NetChat.Models;
using NetChat.Services;
using NetChat.Views;
using Wpf.Ui.Controls;

namespace NetChat;

public partial class MainWindow : FluentWindow
{
    private ShellView? _shell;
    private WelcomeView? _welcome;

    public MainWindow()
    {
        InitializeComponent();
        var settings = App.Settings;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);

        SourceInitialized += (_, _) => ThemeService.UpdateWindow(this);
        Activated += (_, _) => UpdateActive();
        Deactivated += (_, _) => UpdateActive();
        StateChanged += (_, _) => UpdateActive();
        IsVisibleChanged += (_, _) => UpdateActive();
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal && IsLoaded)
            {
                settings.WindowWidth = Math.Round(ActualWidth);
                settings.WindowHeight = Math.Round(ActualHeight);
            }
        };

        // Ctrl+L: kilitle (oturumu kapat, giriş ekranına dön).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control && App.IsSignedIn)
            {
                e.Handled = true;
                ((App)Application.Current).Lock();
            }
        };
    }

    /// <summary>Giriş ekranını gösterir; <paramref name="message"/> verilirse açıklayıcı bir uyarı açar.</summary>
    public void ShowLogin(string? message)
    {
        var welcome = new WelcomeView();
        var previous = (UIElement?)_shell ?? _welcome;
        _shell = null;
        _welcome = welcome;

        async void Swap()
        {
            Host.Children.Clear();
            Host.Children.Add(welcome);
            Anim.FadeIn(welcome, fromY: 18, ms: 320);
            if (message != null)
                await WindowDialogs.ShowAsync("Oturum kapatıldı", MakeMessage(message), "Tamam", secondaryText: null);
            // Yeni sürüm varsa giriş ekranına her dönüşte sorulur.
            OfferUpdate();
        }

        if (previous != null && Host.Children.Contains(previous))
            Anim.FadeOut(previous, toY: -16, ms: 220, done: Swap);
        else
            Swap();
    }

    // ------------------------------------------------------------------ Güncelleme

    /// <summary>Güncellemeden sonraki ilk açılışta: "Güncellendi" bilgisi.</summary>
    public void ShowUpdatedNotice(Version? previous)
    {
        var current = UpdateService.CurrentVersion.ToString(3);
        var text = previous != null
            ? $"NetChat 26, v{previous.ToString(3)} sürümünden v{current} sürümüne güncellendi."
            : $"NetChat 26, v{current} sürümüne güncellendi.";
        _ = WindowDialogs.ShowAsync("Güncellendi", MakeMessage(text), "Tamam", secondaryText: null);
    }

    /// <summary>Giriş ekranındayken yeni sürüm varsa güncellemeyi önerir.</summary>
    public async void OfferUpdate()
    {
        if (App.Updates.Available is not { } update || App.IsSignedIn || App.IsUpdating || WindowDialogs.IsOpen || _welcome == null) return;
        var text = MakeMessage($"NetChat 26 v{update.Version.ToString(3)} yayımlandı. Kullandığınız sürüm: v{UpdateService.CurrentVersion.ToString(3)}.\n\n" +
                               "Güncelleme indirilip kurulduktan sonra uygulama yeniden başlatılır.");
        if (!await WindowDialogs.ShowAsync("Yeni sürüm var", text, "Güncelle", "Daha sonra")) return;
        await RunUpdateAsync(update);
    }

    private async Task RunUpdateAsync(UpdateInfo update)
    {
        var bar = new System.Windows.Controls.ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 4, 0, 10) };
        var status = MakeMessage("İndirme başlıyor…");
        var body = new System.Windows.Controls.StackPanel { Children = { bar, status } };
        App.IsUpdating = true;
        _ = WindowDialogs.ShowAsync($"v{update.Version.ToString(3)} indiriliyor", body, null, null, dismissable: false);
        try
        {
            var progress = new Progress<(long Received, long Total)>(p =>
            {
                var percent = p.Total > 0 ? p.Received * 100.0 / p.Total : 0;
                bar.Value = percent;
                ((System.Windows.Controls.TextBlock)status).Text =
                    $"%{percent:0} · {p.Received / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB";
            });
            var downloaded = await App.Updates.DownloadAsync(update, progress);
            ((System.Windows.Controls.TextBlock)status).Text = "Kuruluyor…";
            UpdateService.InstallAndStart(downloaded);
            App.IsUpdating = false;
            ((App)Application.Current).ExitApplication();
        }
        catch (Exception ex)
        {
            Core.Log.Error("Güncelleme başarısız", ex);
            App.IsUpdating = false;
            WindowDialogs.Dismiss();
            // Tekrar denemek klasöre yazma izni yoksa işe yaramaz.
            var noAccess = ex is UnauthorizedAccessException;
            var reason = noAccess
                ? "Uygulamanın bulunduğu klasöre yazma izni yok. Yeni sürümü elle indirip mevcut exe'nin yerine koyabilirsiniz."
                : ex is TimeoutException or System.Net.Http.HttpRequestException or System.IO.IOException
                    ? "İndirme sırasında internet bağlantısı kesildi. Mevcut sürüm değişmedi."
                    : "İndirilen dosya doğrulanamadı. Mevcut sürüm değişmedi.";
            if (await WindowDialogs.ShowAsync("Güncelleme başarısız", FailureBody(reason, update),
                    noAccess ? null : "Tekrar dene", noAccess ? "Kapat" : "Daha sonra"))
                await RunUpdateAsync(update);
        }
    }

    /// <summary>Hata mesajı ve altında "Manuel indir": son sürümün yayın sayfasını tarayıcıda açar.</summary>
    private UIElement FailureBody(string reason, UpdateInfo update)
    {
        var manual = new Wpf.Ui.Controls.Button
        {
            Content = "Manuel indir",
            Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowDownload20),
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(12, 6, 14, 6),
        };
        manual.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
            WindowDialogs.Dismiss();
        };
        return new System.Windows.Controls.StackPanel { Children = { MakeMessage(reason), manual } };
    }

    /// <summary>Giriş ekranında kullanılacak uyarı/onay diyalogları.</summary>
    public DialogHost Dialogs => WindowDialogs;

    private static UIElement MakeMessage(string text)
    {
        var block = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    /// <summary>Ön kontrol tamamlandı: giriş ekranı kaybolur, ana ekran belirir.</summary>
    public void ShowShell()
    {
        var welcome = _welcome;
        _welcome = null;
        _shell = new ShellView();
        var shell = _shell;

        void Show()
        {
            Host.Children.Clear();
            Host.Children.Add(shell);
            Anim.FadeIn(shell, fromY: 18, ms: 340);
        }

        if (welcome != null && Host.Children.Contains(welcome))
            Anim.FadeOut(welcome, toY: -24, ms: 260, done: Show);
        else
            Show();
    }

    /// <summary>Giriş ekranında verilen profil dosyasını seçili getirir (dosyaya çift tıklandığında).</summary>
    public void OpenProfileFile(string path) => _welcome?.ShowLoginPanel(path);

    /// <summary>Profilde kayıtlı pencere boyutunu uygular (giriş sonrası).</summary>
    public void ApplySavedSize()
    {
        if (WindowState != WindowState.Normal) return;
        Width = Math.Max(MinWidth, App.Settings.WindowWidth);
        Height = Math.Max(MinHeight, App.Settings.WindowHeight);
    }

    public void OpenConversation(Conversation conv) => _shell?.OpenConversation(conv);

    /// <summary>Oturum kapanmadan önce ana ekranın uygulama geneline yaptığı abonelikleri kaldırır.</summary>
    public void DetachShell() => _shell?.Detach();

    private void UpdateActive()
    {
        if (App.IsSignedIn)
            App.Chat.IsWindowActive = IsVisible && IsActive && WindowState != WindowState.Minimized;
    }

    /// <summary>Pencere şu anda kapanıyor (Closing olayı içinde); bu sırada Close() çağrılamaz.</summary>
    public bool IsClosing { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Oturum açıkken kapatma düğmesi uygulamayı sonlandırmaz; bildirim alanında çalışmaya devam eder.
        // Giriş ekranındayken arka planda yapılacak bir şey olmadığından uygulama kapanır.
        // Güncelleme indirilirken kapatılamaz.
        if (App.IsUpdating)
        {
            e.Cancel = true;
            return;
        }
        if (!App.IsExiting && App.IsSignedIn && App.Settings.RunInBackground)
        {
            e.Cancel = true;
            Hide();
            ((App)Application.Current).OnWindowHiddenToTray();
            return;
        }
        base.OnClosing(e);
        if (e.Cancel || App.IsExiting) return;
        IsClosing = true;
        ((App)Application.Current).ExitApplication();
    }

    /// <summary>Görev çubuğu simgesinde okunmamış mesaj rozeti.</summary>
    public void SetUnreadBadge(int count)
    {
        if (count <= 0)
        {
            Taskbar.Overlay = null;
            return;
        }
        var text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)), null, new Point(10, 10), 10, 10);
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                count > 9 ? 9 : 12, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(10 - ft.Width / 2, 10 - ft.Height / 2));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(20, 20, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        Taskbar.Overlay = bmp;
    }
}
