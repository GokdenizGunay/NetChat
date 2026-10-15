using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NetChat.Controls;
using NetChat.Core;
using NetChat.Net;
using NetChat.Services;

namespace NetChat.Views;

/// <summary>Giriş ekranı: profil seçimi/şifre, yeni profil oluşturma ve ağa katılmadan önceki denetimler.</summary>
public partial class WelcomeView : UserControl
{
    private const int MinPasswordLength = 4;
    private UIElement? _current;
    private string? _newFolder;
    private string? _pendingPath;

    public WelcomeView()
    {
        InitializeComponent();
        _current = HomePanel;
        Loaded += OnLoaded;
        Unloaded += (_, _) => App.Network.Changed -= OnNetworkChanged;
        UpdateFolderText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowStatus(App.Network.Current);
        App.Network.Changed += OnNetworkChanged;

        Anim.FadeIn(TitleText, fromY: 14, ms: 420);
        Anim.FadeIn(SubtitleText, fromY: 14, ms: 420, delayMs: 90);
        Anim.FadeIn(StatusPanel, fromY: 14, ms: 420, delayMs: 180);
        if (_pendingPath != null)
        {
            var path = _pendingPath;
            _pendingPath = null;
            HomePanel.Visibility = Visibility.Collapsed;
            _current = null;
            ShowLoginPanel(path);
        }
        else
        {
            Anim.FadeIn(HomePanel, fromY: 14, ms: 420, delayMs: 300);
        }
    }

    private void OnNetworkChanged(NetworkStatus status) => Dispatcher.BeginInvoke(() => ShowStatus(status));

    private void ShowStatus(NetworkStatus status)
    {
        WifiIcon.Visibility = status.Kind == ConnectionKind.Wireless ? Visibility.Visible : Visibility.Collapsed;
        WiredIcon.Visibility = status.Kind == ConnectionKind.Wired ? Visibility.Visible : Visibility.Collapsed;
        OfflineIcon.Visibility = status.Kind == ConnectionKind.None ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status.IsConnected ? "Çevrimiçi" : "Çevrimdışı";
        StatusPanel.ToolTip = status.Kind switch
        {
            ConnectionKind.Wireless => $"Wi-Fi ile bağlı · {status.AdapterName} · {status.Address}",
            ConnectionKind.Wired => $"Kablolu bağlantı · {status.AdapterName} · {status.Address}",
            _ => "Bir yerel ağa bağlı değilsiniz",
        };
    }

    /// <summary>Paneller arasında yumuşak geçiş.</summary>
    private void ShowPanel(UIElement target, Action? afterShown = null)
    {
        if (_current == target) return;
        var previous = _current;
        _current = target;
        void Show()
        {
            target.Visibility = Visibility.Visible;
            Anim.FadeIn(target, fromY: 12, ms: 260);
            afterShown?.Invoke();
        }
        if (previous != null) Anim.FadeOut(previous, toY: -8, ms: 160, done: Show);
        else Show();
    }

    private static void SetError(TextBlock block, string? message)
    {
        block.Text = message ?? "";
        if (message == null) block.Visibility = Visibility.Collapsed;
        else if (block.Visibility != Visibility.Visible) Anim.FadeIn(block, fromY: -4, ms: 180);
    }

    private const string HashWarning = "Profil adında # kullanılamaz.";

    /// <summary>
    /// Ad kutusuna # yazıldığı anda uyarı gösterir, silinince kaldırır (# cihaz etiketinde kullanılır: "elaa#5543").
    /// Diğer kurallar yalnızca "Oluştur"a basınca denetlenir.
    /// </summary>
    private static void WarnOnHash(System.Windows.Controls.TextBox box, TextBlock error)
    {
        if (box.Text.Contains('#')) SetError(error, HashWarning);
        else if (error.Text == HashWarning) SetError(error, null);
    }

    /// <summary>Kullanıcı adı kuralları: 2-32 karakter; harf, rakam, boşluk ve . _ - kullanılabilir.</summary>
    public static string? ValidateName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "Bir ad girin.";
        if (trimmed.Length < 2) return "Ad en az 2 karakter olmalı.";
        if (trimmed.Length > 32) return "Ad en fazla 32 karakter olabilir.";
        if (trimmed.Contains('#')) return HashWarning;
        if (!char.IsLetterOrDigit(trimmed[0])) return "Ad bir harf veya rakamla başlamalı.";
        if (trimmed.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-')))
            return "Yalnızca harf, rakam, boşluk ve . _ - karakterleri kullanılabilir.";
        if (trimmed.Contains("  ")) return "Art arda birden fazla boşluk kullanılamaz.";
        return null;
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => ShowPanel(HomePanel);

    private void OnNewNameChanged(object sender, TextChangedEventArgs e) => WarnOnHash(NewName, NewError);

    private void OnRenameChanged(object sender, TextChangedEventArgs e) => WarnOnHash(RenameBox, RenameError);

    // ------------------------------------------------------------------ Giriş

    private void OnShowLoginClicked(object sender, RoutedEventArgs e) => ShowLoginPanel(null);

    /// <summary>Giriş panelini açar; <paramref name="path"/> verilirse (ör. dosyaya çift tıklandı) o profil seçili gelir.</summary>
    public void ShowLoginPanel(string? path)
    {
        if (!IsLoaded)
        {
            _pendingPath = path;
            return;
        }
        var profiles = ProfileCatalog.List();
        ProfileBox.ItemsSource = profiles;
        ProfileBox.SelectedItem = profiles.FirstOrDefault();
        NoProfilesText.Visibility = ProfileBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoginPassword.Password = "";
        SetError(LoginError, null);
        if (path != null) AddExternal(path);
        ShowPanel(LoginPanel, () => LoginPassword.Focus());
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e) => SetError(LoginError, null);

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Profil dosyasını aç",
            Filter = "NetChat profili (*.nprofile)|*.nprofile",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AddExternal(dialog.FileName);
        LoginPassword.Focus();
    }

    private void AddExternal(string path)
    {
        try
        {
            var header = ProfileFile.ReadHeader(path);
            var items = (ProfileBox.ItemsSource as IEnumerable<ProfileEntry> ?? []).ToList();
            var entry = items.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))
                        ?? new ProfileEntry(path, header.Name, header.Id);
            if (!items.Contains(entry)) items.Add(entry);
            ProfileBox.ItemsSource = items;
            ProfileBox.SelectedItem = entry;
            NoProfilesText.Visibility = Visibility.Collapsed;
            SetError(LoginError, null);
        }
        catch (Exception ex)
        {
            SetError(LoginError, ex is InvalidProfileException ? ex.Message : "Profil dosyası okunamadı.");
        }
    }

    private void OnLoginKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSignInClicked(sender, e);
    }

    private async void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileEntry entry)
        {
            SetError(LoginError, "Bir profil seçin veya bir profil dosyasını aktarın.");
            return;
        }
        var password = LoginPassword.Password ?? "";
        if (password.Length == 0)
        {
            SetError(LoginError, "Şifrenizi girin.");
            return;
        }

        SetError(LoginError, null);
        BusyText.Text = "Profil açılıyor…";
        ShowPanel(BusyPanel);
        try
        {
            var session = await Task.Run(() => ProfileSession.Open(entry.Path, password));
            await BeginAsync(session);
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                WrongPasswordException => "Şifre hatalı.",
                InvalidProfileException => ex.Message,
                FileNotFoundException or DirectoryNotFoundException => "Profil dosyası bulunamadı. Profilin bulunduğu sürücü takılı mı?",
                IOException or UnauthorizedAccessException => "Profil dosyası açılamadı: " + ex.Message,
                _ => "Profil açılamadı: " + ex.Message,
            };
            if (ex is not WrongPasswordException) Log.Error("Giriş başarısız", ex);
            ShowPanel(LoginPanel, () =>
            {
                SetError(LoginError, message);
                LoginPassword.Password = "";
                LoginPassword.Focus();
            });
        }
    }

    // ------------------------------------------------------------------ Yeni profil

    private void OnShowNewClicked(object sender, RoutedEventArgs e)
    {
        NewName.Text = "";
        NewPassword.Password = "";
        NewPasswordRepeat.Password = "";
        SetError(NewError, null);
        ShowPanel(NewPanel, () => NewName.Focus());
    }

    private void UpdateFolderText()
    {
        FolderText.Text = _newFolder ?? "Bu bilgisayar (varsayılan konum)";
        FolderIcon.Symbol = _newFolder == null ? Wpf.Ui.Controls.SymbolRegular.Desktop20 : Wpf.Ui.Controls.SymbolRegular.Folder20;
        ResetFolderButton.Visibility = _newFolder == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChooseFolderClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Profil dosyasının kaydedileceği klasör" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _newFolder = dialog.FolderName;
        UpdateFolderText();
    }

    private void OnResetFolderClicked(object sender, RoutedEventArgs e)
    {
        _newFolder = null;
        UpdateFolderText();
    }

    private void OnNewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnCreateClicked(sender, e);
    }

    private async void OnCreateClicked(object sender, RoutedEventArgs e)
    {
        var name = NewName.Text.Trim();
        var password = NewPassword.Password ?? "";
        var error = ValidateName(name)
                    ?? (password.Length < MinPasswordLength ? $"Şifre en az {MinPasswordLength} karakter olmalı." : null)
                    ?? (password != NewPasswordRepeat.Password ? "Şifreler eşleşmiyor." : null)
                    ?? (_newFolder != null && !Directory.Exists(_newFolder) ? "Seçilen klasöre erişilemiyor." : null)
                    ?? (ProfileCatalog.NameExists(name, _newFolder) ? "Bu adla kayıtlı bir profil zaten var. Farklı bir ad seçin." : null);
        if (error != null)
        {
            SetError(NewError, error);
            return;
        }

        SetError(NewError, null);
        BusyText.Text = "Profil oluşturuluyor…";
        ShowPanel(BusyPanel);
        var folder = _newFolder;
        try
        {
            var session = await Task.Run(() => ProfileSession.Create(name, password, folder,
                settingsJson: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { DisplayName = name, KeepMessageHistory = false })));
            await BeginAsync(session);
        }
        catch (Exception ex)
        {
            Log.Error("Profil oluşturulamadı", ex);
            ShowPanel(NewPanel, () => SetError(NewError, "Profil oluşturulamadı: " + ex.Message));
        }
    }

    // ------------------------------------------------------------------ Ağa katılma

    /// <summary>Profil açıldı: ağı denetle (aynı profil / aynı ad), sorun yoksa ana ekrana geç.</summary>
    private async Task BeginAsync(ProfileSession session)
    {
        BusyText.Text = "Ağ denetleniyor…";
        var app = (App)Application.Current;
        var result = await app.BeginSessionAsync(session);
        switch (result)
        {
            case PreflightResult.Ok:
                app.CompleteSession();
                break;
            case PreflightResult.NameTaken:
                RenameText.Text = $"Ağda \"{App.Settings.DisplayName}\" adını kullanan başka biri var. Ağa katılmak için farklı bir ad seçin.";
                RenameBox.Text = App.Settings.DisplayName;
                SetError(RenameError, null);
                ShowPanel(RenamePanel, () =>
                {
                    RenameBox.Focus();
                    RenameBox.SelectAll();
                });
                break;
            // DuplicateSession: oturum App tarafından kapatıldı ve uyarı gösterildi.
        }
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRenameClicked(sender, e);
    }

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (!App.IsSignedIn) return;
        var name = RenameBox.Text.Trim();
        var error = ValidateName(name) ?? (App.Chat.IsNameTaken(name) ? "Bu ad da ağda kullanılıyor." : null);
        if (error != null)
        {
            SetError(RenameError, error);
            return;
        }
        App.Settings.DisplayName = name;
        ((App)Application.Current).CompleteSession();
    }

    private void OnRenameCancelClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).EndSession(null);
}
