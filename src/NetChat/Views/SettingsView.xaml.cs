using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NetChat.Controls;
using NetChat.Core;
using NetChat.Models;
using NetChat.Net;
using Wpf.Ui.Controls;

namespace NetChat.Views;

public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings = App.Settings;

    public SettingsView()
    {
        InitializeComponent();
        DataContext = _settings;

        ThemeBox.SelectedIndex = (int)_settings.Theme;
        MyCode.Code = Identity.Fingerprint(App.Identity.PublicKey);
        MyId.Text = $"Profil kimliği: {App.Identity.Id}";
        AboutText.Text = $"Sürüm {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        UpdateStatusText.Text = "Yeni bir sürüm olup olmadığını şimdi denetleyin";
        ShowUpdateState();
        App.Updates.AvailableChanged += OnUpdateAvailableChanged;

        LoadProfile();
        UpdateProfileLocation();
        LoadGroupAdd();
        HistoryToggle.IsChecked = _settings.KeepMessageHistory;
        _settings.PropertyChanged += OnSettingsChanged;
        App.Network.Changed += OnNetworkChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ShowNetwork(App.Network.Current);
        };
        ShowNetwork(App.Network.Current);
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.DisplayName)) LoadProfile();
    }

    private void OnNetworkChanged(NetworkStatus status) => Dispatcher.BeginInvoke(() => ShowNetwork(status));

    /// <summary>Oturum kapanırken abonelikleri kaldırır.</summary>
    public void Detach()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        App.Network.Changed -= OnNetworkChanged;
        App.Updates.AvailableChanged -= OnUpdateAvailableChanged;
    }

    public void FocusProfile()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ProfileCard.BringIntoView();
            NameBox.Focus();
            NameBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void LoadProfile()
    {
        NameBox.Text = _settings.DisplayName;
        ProfileAvatar.Text = Conversation.MakeInitials(_settings.DisplayName);
        ProfileAvatar.Key = App.Identity.Id;
        SaveNameButton.IsEnabled = false;
    }

    // ------------------------------------------------------------------ Mesaj geçmişi

    private async void OnHistoryToggleClicked(object sender, RoutedEventArgs e)
    {
        var on = HistoryToggle.IsChecked == true;
        if (on)
        {
            _settings.KeepMessageHistory = true;
            return;
        }
        // Kapatmak mevcut geçmişi profilden siler: onay iste.
        HistoryToggle.IsChecked = true;
        if (FindShell() is not { } shell) return;
        var ok = await shell.ConfirmAsync("Mesaj geçmişi silinsin mi?",
            "Okunmuş mesajlar profil dosyasından silinecek. Bu oturumda ekranda görünmeye devam ederler, oturum kapanınca kaybolurlar. " +
            "Okunmamış ve henüz iletilemeyen mesajlar saklanmaya devam eder.",
            "Geçmişi sakla'yı kapat", danger: true);
        if (!ok) return;
        HistoryToggle.IsChecked = false;
        _settings.KeepMessageHistory = false;
    }

    // ------------------------------------------------------------------ Gruplara ekleme izni

    private bool _loadingGroupAdd;

    private void LoadGroupAdd()
    {
        _loadingGroupAdd = true;
        (_settings.GroupAddMode switch
        {
            GroupAddMode.Nobody => GroupAddNobody,
            GroupAddMode.Only => GroupAddOnly,
            GroupAddMode.Except => GroupAddExcept,
            _ => GroupAddEveryone,
        }).IsChecked = true;
        _loadingGroupAdd = false;
        UpdateGroupAddTexts();
    }

    private void UpdateGroupAddTexts()
    {
        var mode = _settings.GroupAddMode;
        var only = _settings.GroupAddOnly.Count;
        var except = _settings.GroupAddExcept.Count;
        GroupAddSummary.Text = "Beni gruplara ekleyebilecekler: " + mode switch
        {
            GroupAddMode.Nobody => "Hiç kimse",
            GroupAddMode.Only => only == 0 ? "Hiç kimse (liste boş)" : $"Yalnızca {only} kişi",
            GroupAddMode.Except => except == 0 ? "Herkes" : $"{except} kişi hariç herkes",
            _ => "Herkes",
        };
        GroupAddOnlyButton.Content = $"Kişileri seç ({only})";
        GroupAddExceptButton.Content = $"Kişileri seç ({except})";
        GroupAddOnlyButton.Visibility = mode == GroupAddMode.Only ? Visibility.Visible : Visibility.Collapsed;
        GroupAddExceptButton.Visibility = mode == GroupAddMode.Except ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnGroupAddModeChecked(object sender, RoutedEventArgs e)
    {
        if (_loadingGroupAdd || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<GroupAddMode>(tag, out var mode)) return;
        _settings.GroupAddMode = mode;
        UpdateGroupAddTexts();
        // Liste boşsa kişi seçimini hemen aç.
        if (mode == GroupAddMode.Only && _settings.GroupAddOnly.Count == 0) await PickGroupAddListAsync(GroupAddMode.Only);
        else if (mode == GroupAddMode.Except && _settings.GroupAddExcept.Count == 0) await PickGroupAddListAsync(GroupAddMode.Except);
    }

    private async void OnGroupAddOnlyClicked(object sender, RoutedEventArgs e) => await PickGroupAddListAsync(GroupAddMode.Only);

    private async void OnGroupAddExceptClicked(object sender, RoutedEventArgs e) => await PickGroupAddListAsync(GroupAddMode.Except);

    private async Task PickGroupAddListAsync(GroupAddMode mode)
    {
        if (FindShell() is not { } shell) return;
        var current = mode == GroupAddMode.Only ? _settings.GroupAddOnly : _settings.GroupAddExcept;
        var picker = new PeerPicker(App.Chat.Peers, forGroup: false, preselected: current);
        var title = mode == GroupAddMode.Only ? "Yalnızca şu kişiler" : "Şu kişiler hariç herkes";
        if (!await shell.DialogHost.ShowAsync(title, picker, "Kaydet")) return;
        // Listede olup şu an tanınmayan (ağda hiç görülmemiş) kişileri koru.
        var known = App.Chat.Peers.Select(p => p.Id).ToHashSet();
        List<string> ids = [.. current.Where(id => !known.Contains(id)), .. picker.Selected.Select(p => p.Id)];
        if (mode == GroupAddMode.Only) _settings.GroupAddOnly = ids;
        else _settings.GroupAddExcept = ids;
        UpdateGroupAddTexts();
    }

    private ShellView? FindShell()
    {
        DependencyObject? d = this;
        while (d != null && d is not ShellView) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as ShellView;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        var error = WelcomeView.ValidateName(NameBox.Text);
        var changed = NameBox.Text.Trim() != _settings.DisplayName;
        SaveNameButton.IsEnabled = changed && error == null;
        NameError.Text = error ?? "";
        if (error != null && changed && NameError.Visibility != Visibility.Visible) Anim.FadeIn(NameError, fromY: -4, ms: 160);
        else if ((error == null || !changed) && NameError.Visibility == Visibility.Visible) Anim.FadeOut(NameError, toY: -4, ms: 120);
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SaveNameButton.IsEnabled) SaveName();
    }

    private void OnSaveNameClicked(object sender, RoutedEventArgs e) => SaveName();

    private void SaveName()
    {
        if (WelcomeView.ValidateName(NameBox.Text) != null) return;
        _settings.DisplayName = NameBox.Text.Trim();
        Anim.PopIn(ProfileAvatar);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (Enum.TryParse<AppThemeMode>(tag, out var mode) && mode != _settings.Theme) _settings.Theme = mode;
    }

    private void ShowNetwork(NetworkStatus status)
    {
        NetIcon.Symbol = status.Kind switch
        {
            ConnectionKind.Wireless => SymbolRegular.Wifi124,
            ConnectionKind.Wired => SymbolRegular.PlugConnected24,
            _ => SymbolRegular.GlobeProhibited24,
        };
        NetTitle.Text = status.Kind switch
        {
            ConnectionKind.Wireless => "Wi-Fi ile bağlı",
            ConnectionKind.Wired => "Kablolu ağa bağlı",
            _ => "Yerel ağ bağlantısı yok",
        };
        var online = App.Chat.Peers.Count(p => p.IsOnline);
        NetDesc.Text = status.IsConnected
            ? $"{status.AdapterName} · IP {status.Address} · TCP {App.Chat.TcpPort} · UDP keşif {DiscoveryService.Port} · {online} kişi çevrimiçi"
            : "Diğer cihazları görmek için bir Wi-Fi veya kablolu ağa bağlanın.";
    }

    private async void OnRescanClicked(object sender, RoutedEventArgs e)
    {
        await App.Chat.RescanAsync();
        await Task.Delay(1200);
        ShowNetwork(App.Network.Current);
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.EffectiveDownloadFolder);
            Process.Start(new ProcessStartInfo(_settings.EffectiveDownloadFolder) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void OnChangeFolderClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "İndirme klasörünü seçin",
            InitialDirectory = Directory.Exists(_settings.EffectiveDownloadFolder) ? _settings.EffectiveDownloadFolder : null,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _settings.DownloadFolder = dialog.FolderName;
    }

    private void OnExitClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApplication();

    // ------------------------------------------------------------------ Güncelleme

    private void OnUpdateAvailableChanged() => Dispatcher.BeginInvoke(ShowUpdateState);

    /// <summary>Yeni sürüm varsa kart en üste taşınır ve düğmesi "Güncelle" olur; yoksa Hakkında bölümünde durur.</summary>
    private void ShowUpdateState()
    {
        var update = App.Updates.Available;
        var target = update != null ? UpdateTopHost : UpdateBottomHost;
        if (UpdateCard.Parent != target)
        {
            ((Panel)UpdateCard.Parent).Children.Remove(UpdateCard);
            target.Children.Add(UpdateCard);
        }
        UpdateTopHost.Visibility = update != null ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.Content = update != null ? "Güncelle" : "Şimdi denetle";
        CheckUpdateButton.Appearance = update != null ? ControlAppearance.Primary : ControlAppearance.Secondary;
        if (update != null) UpdateStatusText.Text = $"Yeni sürüm var: v{update.Version.ToString(3)} · Kullandığınız sürüm: v{Services.UpdateService.CurrentVersion.ToString(3)}";
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        // Yeni sürüm zaten biliniyorsa doğrudan güncelleme diyaloğu.
        if (App.Updates.Available is { } known)
        {
            if (Window.GetWindow(this) is MainWindow owner) await owner.PromptUpdateAsync(known);
            return;
        }
        if (!Services.UpdateService.IsSupported)
        {
            UpdateStatusText.Text = "Otomatik güncelleme yalnızca yayımlanan NetChat.exe sürümünde kullanılabilir";
            return;
        }
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Denetleniyor…";
        try
        {
            if (!await App.Updates.CheckAsync())
            {
                UpdateStatusText.Text = "Denetlenemedi. İnternet bağlantınızı kontrol edip tekrar deneyin";
                return;
            }
            if (App.Updates.Available is not { } update)
            {
                UpdateStatusText.Text = $"NetChat güncel (v{Services.UpdateService.CurrentVersion.ToString(3)})";
                return;
            }
            if (Window.GetWindow(this) is MainWindow window) await window.PromptUpdateAsync(update);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ Profil konumu

    private void UpdateProfileLocation()
    {
        if (App.Session is not { } session) return;
        ProfilePathText.Text = session.IsInDefaultLocation ? "Bu bilgisayar (varsayılan konum)" : session.FilePath;
        MoveHomeButton.Visibility = session.IsInDefaultLocation ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnMoveProfileClicked(object sender, RoutedEventArgs e)
    {
        if (App.Session is not { } session) return;
        var dialog = new OpenFolderDialog { Title = "Profilin taşınacağı klasör" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        MoveProfile(session, dialog.FolderName);
    }

    private void OnMoveHomeClicked(object sender, RoutedEventArgs e)
    {
        if (App.Session is { } session) MoveProfile(session, null);
    }

    private void MoveProfile(ProfileSession session, string? folder)
    {
        try
        {
            session.MoveTo(folder);
            UpdateProfileLocation();
            App.Chat.ShowInfo(folder == null ? "Profil bu bilgisayara taşındı." : $"Profil taşındı: {session.FilePath}");
        }
        catch (Exception ex)
        {
            Log.Error("Profil taşınamadı", ex);
            App.Chat.ShowInfo("Profil taşınamadı: " + ex.Message);
        }
    }
}
