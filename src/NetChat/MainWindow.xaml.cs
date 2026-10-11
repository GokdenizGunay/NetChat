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

        void Swap()
        {
            Host.Children.Clear();
            Host.Children.Add(welcome);
            Anim.FadeIn(welcome, fromY: 18, ms: 320);
            if (message != null)
                _ = WindowDialogs.ShowAsync("Oturum kapatıldı", MakeMessage(message), "Tamam", secondaryText: null);
        }

        if (previous != null && Host.Children.Contains(previous))
            Anim.FadeOut(previous, toY: -16, ms: 220, done: Swap);
        else
            Swap();
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
