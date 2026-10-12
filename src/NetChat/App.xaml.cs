using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NetChat.Core;
using NetChat.Services;

namespace NetChat;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    /// <summary>Açık profilin kimliği (yalnızca oturum açıkken).</summary>
    public static Identity Identity => Session?.Identity ?? throw new InvalidOperationException("Oturum açık değil.");
    public static ChatService Chat { get; private set; } = null!;
    public static NotificationService Notifications { get; private set; } = null!;
    public static NetChat.Net.NetworkStatusService Network { get; private set; } = null!;
    public static bool IsExiting { get; private set; }
    public static UpdateService Updates { get; } = new();
    /// <summary>Güncelleme indirilirken uygulama kapatılamaz ve kullanılamaz.</summary>
    public static bool IsUpdating { get; set; }

    private Mutex? _mutex;
    private TrayService? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? profile = null;
        string? openPath = null;
        var minimized = false;
        (int Pid, string Exe)? updatedFrom = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--profile" && i + 1 < e.Args.Length) profile = e.Args[++i];
            else if (e.Args[i] == "--updated-from" && i + 2 < e.Args.Length && int.TryParse(e.Args[i + 1], out var oldPid))
            {
                updatedFrom = (oldPid, e.Args[i + 2]);
                i += 2;
            }
            else if (e.Args[i] == "--minimized") minimized = true;
            else if (e.Args[i] == "--log") AppPaths.LoggingEnabled = true;
            else if (e.Args[i].EndsWith(ProfileFile.Extension, StringComparison.OrdinalIgnoreCase)) openPath = Path.GetFullPath(e.Args[i]);
        }
        AppPaths.Initialize(profile);
        // Güncellemeden sonra: eski sürüm kapanıp tek örnek kilidini bırakana kadar bekle, eski exe'yi sil.
        Version? updatedFromVersion = null;
        var justUpdated = false;
        if (updatedFrom is { } old)
        {
            updatedFromVersion = UpdateService.FinishUpdate(old.Pid, old.Exe);
            justUpdated = true;
        }

        // Tek örnek: ikinci kez açılırsa (ör. bir .nprofile dosyasına çift tıklandı) isteği mevcut örneğe ilet.
        _mutex = new Mutex(true, $@"Local\{AppPaths.AppId}-{AppPaths.Profile}", out var isFirst);
        if (!isFirst)
        {
            InstancePipe.Send(openPath ?? "");
            Shutdown();
            return;
        }
        InstancePipe.Listen(message =>
        {
            if (message.StartsWith(InstancePipe.ToastPrefix, StringComparison.Ordinal))
                Notifications?.HandleForwarded(message[InstancePipe.ToastPrefix.Length..]);
            else
                Dispatcher.BeginInvoke(() => OnExternalOpen(message.Length > 0 ? message : null));
        });

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("İşlenmeyen hata", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Gözlenmeyen görev hatası", args.Exception);
            args.SetObserved();
        };

        Settings = AppSettings.Create();
        Log.Info($"Başlatılıyor. Örnek: {AppPaths.Profile}");
        ThemeService.Apply(Settings.Theme);

        Network = new NetChat.Net.NetworkStatusService();
        Notifications = new NotificationService(Settings);
        // Önceki oturum düzgün kapanmadıysa (ör. Görev Yöneticisi'nden sonlandırıldıysa) kalan bildirimleri ve
        // bildirim görsellerini temizle.
        NotificationService.ClearAll();

        _tray = new TrayService();
        _tray.OpenRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;
        Notifications.Fallback += (title, text) => _tray.ShowBalloon(title, text);
        Notifications.Activated += (action, convId, text) => Dispatcher.BeginInvoke(() => OnToastActivated(action, convId, text));
        Network.Changed += status => Dispatcher.BeginInvoke(() => OnNetworkChanged(status));

        Settings.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(AppSettings.Theme):
                    ThemeService.Apply(Settings.Theme);
                    break;
                // Kayıt defteri yalnızca kullanıcı ayarı değiştirdiğinde güncellenir (çıkışta ayarların sıfırlanması etkilemez).
                case nameof(AppSettings.StartWithWindows) when IsSignedIn:
                    StartupService.Apply(Settings.StartWithWindows);
                    break;
            }
        };
        Settings.ProfileChanged += () => Session?.UpdateSettings(Settings.SerializeForProfile(), Settings.DisplayName);
        FileAssociation.Register();

        // Profiller şifreli olduğu için giriş yapılmadan çalışılamaz: her zaman giriş ekranıyla açılır.
        _window = new MainWindow();
        MainWindow = _window;
        _window.Show();
        _window.ShowLogin(null);
        if (openPath != null) _window.OpenProfileFile(openPath);
        // Yalnızca güncellemeden sonraki ilk açılış "--updated-from" ile başlatılır.
        if (justUpdated) _window.ShowUpdatedNotice(updatedFromVersion);
        _ = CheckForUpdatesAsync();
        // Windows ile başlatıldıysa: giriş için şifre gerektiğinden görev çubuğunda simge durumunda bekle.
        if (minimized) _window.WindowState = WindowState.Minimized;
    }

    // ------------------------------------------------------------------ Oturum

    /// <summary>Açık profil oturumu; giriş ekranındayken null.</summary>
    public static ProfileSession? Session { get; private set; }

    public static bool IsSignedIn => Session != null && Chat != null;

    private DispatcherTimer? _networkLossTimer;

    /// <summary>
    /// Profil açıldıktan sonra çağrılır: ayarları uygular, mesajlaşma servisini kurar ve ağ ön kontrolünü yapar.
    /// Sonuç <see cref="PreflightResult.Ok"/> değilse giriş ekranı kullanıcıyı yönlendirir.
    /// </summary>
    public async Task<PreflightResult> BeginSessionAsync(ProfileSession session)
    {
        Session = session;
        Settings.ApplyProfile(session.InitialPayload.SettingsJson);
        if (string.IsNullOrWhiteSpace(Settings.DisplayName)) Settings.DisplayName = session.Header.Name;

        var chat = new ChatService(session.Identity, Settings, Dispatcher,
            ChatStore.Deserialize(session.InitialPayload.StoreJson), bytes => session.UpdateStore(bytes));
        Chat = chat;
        chat.MessageNotification += OnMessageNotification;
        chat.PropertyChanged += OnChatPropertyChanged;
        chat.DuplicateSessionDetected += OnDuplicateSession;
        session.Lost += reason => Dispatcher.BeginInvoke(() => EndSession(reason));

        Log.Info($"Oturum açılıyor: {session.Header.Name} ({session.Identity.Id[..8]})");
        var result = await chat.StartAsync();
        if (result == PreflightResult.DuplicateSession)
            EndSession("Bu profil ağda başka bir oturumda zaten açık. Aynı profil aynı anda yalnızca bir yerde kullanılabilir; " +
                       "son açılan oturuma izin verilmez.");
        return result;
    }

    /// <summary>Ön kontrol tamam: ağa katıl ve ana ekranı göster.</summary>
    public void CompleteSession()
    {
        if (Chat == null) return;
        Session?.Activate();
        Chat.JoinNetwork();
        if (Settings.StartWithWindows) StartupService.Apply(true);
        _window?.ApplySavedSize();
        _tray?.SetUnread(Chat.TotalUnread);
        _window?.SetUnreadBadge(Chat.TotalUnread);
        _window?.ShowShell();
    }

    /// <summary>Kilit (Ctrl+L): oturumu kapatır, giriş ekranına döner. Bildirim ve mesaj alınmaz.</summary>
    public void Lock() => EndSession(null);

    /// <summary>Oturumu kapatır ve giriş ekranına döner; <paramref name="reason"/> verilirse açıklama gösterilir.</summary>
    public void EndSession(string? reason)
    {
        _networkLossTimer?.Stop();
        var chat = Chat;
        var session = Session;
        if (chat == null && session == null) return;
        _window?.DetachShell();
        Chat = null!;
        Session = null;

        if (chat != null)
        {
            chat.MessageNotification -= OnMessageNotification;
            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.DuplicateSessionDetected -= OnDuplicateSession;
            try
            {
                chat.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Oturum kapatma hatası", ex);
            }
        }
        if (session != null)
        {
            if (reason == null) Settings.LastActive = DateTimeOffset.Now;
            session.UpdateSettings(Settings.SerializeForProfile(), Settings.DisplayName);
            session.Dispose();
        }
        Settings.ResetProfileFields();
        NotificationService.ClearAll();
        _tray?.SetUnread(0);
        _window?.SetUnreadBadge(0);
        Log.Info("Oturum kapatıldı" + (reason != null ? $": {reason}" : ""));
        if (reason != null) ShowMainWindow();
        _window?.ShowLogin(reason);
    }

    /// <summary>Uygulama kapanırken: oturumu "bye" ile kapatıp profili son kez kaydeder.</summary>
    private void ShutdownSession()
    {
        var chat = Chat;
        var session = Session;
        Chat = null!;
        Session = null;
        chat?.Dispose();
        if (session != null)
        {
            Settings.LastActive = DateTimeOffset.Now;
            session.UpdateSettings(Settings.SerializeForProfile(), Settings.DisplayName);
            session.Dispose();
        }
        NotificationService.ClearAll();
    }

    private void OnDuplicateSession() =>
        EndSession("Bu profil ağda başka bir cihazda daha önce açılmış. Aynı profil aynı anda yalnızca bir yerde kullanılabilir.");

    private void OnMessageNotification(Models.Conversation conv, Models.ChatMessage msg) => Notifications.ShowMessage(conv, msg);

    private void OnNetworkChanged(NetChat.Net.NetworkStatus status)
    {
        if (!IsSignedIn) return;
        if (status.IsConnected)
        {
            _networkLossTimer?.Stop();
            // Ağa yeniden bağlanıldığında diğer cihazları beklemeden bul.
            _ = Chat.RescanAsync();
            return;
        }
        // Kısa kesintiler (ör. DHCP yenilemesi) oturumu kapatmasın.
        _networkLossTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _networkLossTimer.Tick -= OnNetworkLossTimeout;
        _networkLossTimer.Tick += OnNetworkLossTimeout;
        _networkLossTimer.Start();
    }

    private void OnNetworkLossTimeout(object? sender, EventArgs e)
    {
        _networkLossTimer?.Stop();
        if (IsSignedIn && !Network.Current.IsConnected)
            EndSession("Ağ bağlantısı kesildi. Yeniden bağlandığınızda tekrar giriş yapabilirsiniz.");
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatService.TotalUnread) && Chat != null)
        {
            _tray?.SetUnread(Chat.TotalUnread);
            _window?.SetUnreadBadge(Chat.TotalUnread);
        }
    }

    private void OnToastActivated(string action, string convId, string? text)
    {
        var conv = IsSignedIn ? Chat.FindConversation(convId) : null;
        if (conv == null)
        {
            ShowMainWindow();
            return;
        }
        switch (action)
        {
            case "reply":
                Chat.SendText(conv, text ?? "");
                Chat.MarkConversationRead(conv);
                break;
            case "markread":
                Chat.MarkConversationRead(conv);
                break;
            default:
                ShowMainWindow();
                _window?.OpenConversation(conv);
                break;
        }
    }
    private async Task CheckForUpdatesAsync()
    {
        await Updates.CheckAsync();
        // Giriş ekranındaysak hemen sor; değilse giriş ekranına dönüldüğünde sorulur.
        if (Updates.Available != null && !IsSignedIn) _window?.OfferUpdate();
    }

    /// <summary>Başka bir örnekten gelen istek: pencereyi göster; bir profil dosyası verildiyse giriş ekranında seç.</summary>
    private void OnExternalOpen(string? path)
    {
        ShowMainWindow();
        if (path == null) return;
        if (Session == null) _window?.OpenProfileFile(path);
        else if (IsSignedIn && !string.Equals(Path.GetFullPath(Session.FilePath), path, StringComparison.OrdinalIgnoreCase))
            Chat.ShowInfo("Başka bir profili açmak için önce oturumu kilitleyin (Ctrl+L).");
    }

    public void ShowMainWindow()
    {
        if (_window == null || IsExiting) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Başka bir uygulama odaktaysa öne getirmek için kısa süreli Topmost hilesi.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    /// <summary>Pencere kapatıldığında arka planda çalışmaya devam edildiğini bir kez bildirir.</summary>
    public void OnWindowHiddenToTray()
    {
        if (Settings.BackgroundHintShown) return;
        Settings.BackgroundHintShown = true;
        // Kapatma olayının içinden değil, olay bittikten sonra göster.
        Dispatcher.BeginInvoke(() => Notifications.ShowInfo(AppPaths.AppName + " arka planda çalışıyor",
            "Yeni mesajlarda bildirim alacaksınız. Tamamen kapatmak için bildirim alanındaki simgeye sağ tıklayın."));
    }

    public void ExitApplication()
    {
        if (IsExiting || IsUpdating) return;
        IsExiting = true;
        try
        {
            ShutdownSession();
        }
        catch (Exception ex)
        {
            Log.Error("Kapatma hatası", ex);
        }
        _tray?.Dispose();
        try
        {
            // Pencerenin kendi kapanışı sırasında çağrıldıysa (giriş ekranında X) tekrar kapatılamaz; Shutdown yine çalışmalı.
            if (_window is { IsClosing: false }) _window.Close();
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("Pencere kapatılamadı", ex);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try
        {
            if (!IsExiting) ShutdownSession();
            _mutex?.ReleaseMutex();
        }
        catch
        {
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Arayüz hatası", e.Exception);
        e.Handled = true;
    }
}
