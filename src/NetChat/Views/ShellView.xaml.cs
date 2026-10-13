using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using NetChat.Controls;
using NetChat.Models;
using NetChat.Net;
using NetChat.Services;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace NetChat.Views;

public partial class ShellView : UserControl
{
    private readonly ChatService _chat = App.Chat;
    private readonly ListCollectionView _conversations;
    private readonly ListCollectionView _people;
    private UIElement? _currentContent;
    private Conversation? _menuTarget;
    private bool _suppressSelection;

    public ShellView()
    {
        InitializeComponent();

        _conversations = new ListCollectionView(_chat.Conversations);
        _conversations.SortDescriptions.Add(new SortDescription(nameof(Conversation.IsPinned), ListSortDirection.Descending));
        _conversations.SortDescriptions.Add(new SortDescription(nameof(Conversation.LastActivity), ListSortDirection.Descending));
        _conversations.IsLiveSorting = true;
        _conversations.LiveSortingProperties.Add(nameof(Conversation.IsPinned));
        _conversations.LiveSortingProperties.Add(nameof(Conversation.LastActivity));
        _conversations.IsLiveFiltering = true;
        _conversations.LiveFilteringProperties.Add(nameof(Conversation.Title));
        ConversationList.ItemsSource = _conversations;

        // Yalnızca bu oturumda ağda görülen kişiler; Çevrimiçi / Boşta / Çevrimdışı bölümlerinde.
        _people = new ListCollectionView(_chat.Peers) { Filter = PeopleFilter };
        _people.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Peer.PresenceGroup)));
        _people.SortDescriptions.Add(new SortDescription(nameof(Peer.PresenceGroupOrder), ListSortDirection.Ascending));
        _people.SortDescriptions.Add(new SortDescription(nameof(Peer.Name), ListSortDirection.Ascending));
        _people.IsLiveSorting = true;
        _people.LiveSortingProperties.Add(nameof(Peer.PresenceGroupOrder));
        _people.LiveSortingProperties.Add(nameof(Peer.Name));
        _people.IsLiveGrouping = true;
        _people.LiveGroupingProperties.Add(nameof(Peer.PresenceGroup));
        _people.IsLiveFiltering = true;
        _people.LiveFilteringProperties.Add(nameof(Peer.SeenThisSession));
        _people.LiveFilteringProperties.Add(nameof(Peer.Name));
        PeopleList.ItemsSource = _people;

        _chat.Conversations.CollectionChanged += (_, _) => UpdateEmptyStates();
        _chat.Peers.CollectionChanged += OnPeersChanged;
        foreach (var p in _chat.Peers) p.PropertyChanged += OnPeerPropertyChanged;
        _chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatService.TotalUnread)) UpdateRailBadge();
        };
        _chat.Info += ShowInfo;
        App.Settings.PropertyChanged += OnSettingsChanged;
        UpdateBell();
        NotificationService.SystemSettingChanged += OnSystemNotificationSettingChanged;
        App.Network.Changed += OnNetworkChanged;

        ConversationList.PreviewMouseRightButtonDown += OnConversationRightClick;
        Chat.CloseRequested += () => ShowContent(EmptyState);
        Chat.InfoOpenChanged += UpdateListWidth;
        SizeChanged += (_, _) => UpdateListWidth();

        _currentContent = EmptyState;
        UpdateListWidth();
        PeopleList.SizeChanged += (_, _) => PeopleList.Tag = PeopleList.ActualWidth < 300 ? "narrow" : null;
        UpdateProfile();
        ShowNetwork(App.Network.Current);
        UpdateEmptyStates();
        UpdateRailBadge();
        UpdateListTitle();
        App.Updates.AvailableChanged += OnUpdateAvailableChanged;
        UpdateSettingsBadge();
    }

    // ------------------------------------------------------------------ Genel

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(App.Settings.DisplayName)) UpdateProfile();
        if (e.PropertyName == nameof(App.Settings.NotificationsMuted)) UpdateBell();
    }

    private void OnNetworkChanged(NetworkStatus status) => Dispatcher.BeginInvoke(() => ShowNetwork(status));

    /// <summary>
    /// Oturum kapanırken çağrılır: uygulama ömrü boyunca yaşayan nesnelere (ayarlar, ağ durumu) yapılan
    /// abonelikleri kaldırır; böylece kapanmış oturumun görünümleri artık tepki vermez.
    /// </summary>
    public void Detach()
    {
        App.Settings.PropertyChanged -= OnSettingsChanged;
        App.Network.Changed -= OnNetworkChanged;
        Settings.Detach();
        NotificationService.SystemSettingChanged -= OnSystemNotificationSettingChanged;
        App.Updates.AvailableChanged -= OnUpdateAvailableChanged;
        Dialogs.Visibility = Visibility.Collapsed;
    }

    private void UpdateProfile()
    {
        MyAvatar.Text = Conversation.MakeInitials(App.Settings.DisplayName);
        MyAvatar.Key = _chat.MyId;
        ProfileButton.ToolTip = App.Settings.DisplayName;
    }

    private void ShowNetwork(NetworkStatus status)
    {
        NetWifi.Visibility = status.Kind == ConnectionKind.Wireless ? Visibility.Visible : Visibility.Collapsed;
        NetWired.Visibility = status.Kind == ConnectionKind.Wired ? Visibility.Visible : Visibility.Collapsed;
        NetOffline.Visibility = status.Kind == ConnectionKind.None ? Visibility.Visible : Visibility.Collapsed;
        NetText.Text = status.IsConnected ? $"Çevrimiçi · {status.Address}" : "Çevrimdışı";
    }

    private void OnUpdateAvailableChanged() => Dispatcher.BeginInvoke(UpdateSettingsBadge);

    /// <summary>Yeni sürüm olduğu sürece Ayarlar simgesinde nokta durur.</summary>
    private void UpdateSettingsBadge()
    {
        var show = App.Updates.Available != null;
        if (show && SettingsBadge.Visibility != Visibility.Visible) Anim.PopIn(SettingsBadge);
        else if (!show) SettingsBadge.Visibility = Visibility.Collapsed;
        SettingsTab.ToolTip = show ? "Ayarlar · Yeni sürüm var" : "Ayarlar";
    }

    private void UpdateRailBadge()
    {
        var show = _chat.TotalUnread > 0;
        if (show && RailBadge.Visibility != Visibility.Visible) Anim.PopIn(RailBadge);
        else if (!show) RailBadge.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyStates()
    {
        var chats = ChatsTab.IsChecked == true || SettingsTab.IsChecked == true && ConversationList.Visibility == Visibility.Visible;
        EmptyChats.Visibility = chats && _chat.Conversations.Count == 0 && string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyPeople.Visibility = PeopleList.Visibility == Visibility.Visible && !_chat.Peers.Any(p => p.SeenThisSession)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Ağdaki kişiler simgesinde çevrimiçi kişi sayısı.</summary>
    private void UpdatePeopleBadge()
    {
        var online = _chat.Peers.Count(p => p.IsOnline);
        PeopleBadgeText.Text = online > 99 ? "99+" : online.ToString();
        var tip = online > 0 ? $"Ağdaki kişiler ({online} çevrimiçi)" : "Ağdaki kişiler";
        PeopleTab.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetHelpText(PeopleTab, tip);
        if (online > 0 && PeopleBadge.Visibility != Visibility.Visible) Anim.PopIn(PeopleBadge);
        else if (online == 0) PeopleBadge.Visibility = Visibility.Collapsed;
    }

    private void UpdateListTitle()
    {
        UpdatePeopleBadge();
        if (PeopleList.Visibility == Visibility.Visible)
        {
            var online = _chat.Peers.Count(p => p.IsOnline);
            ListTitle.Text = online > 0 ? $"Ağdaki kişiler ({online})" : "Ağdaki kişiler";
        }
        else
        {
            ListTitle.Text = "Sohbetler";
        }
    }

    private void OnPeersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Peer p in e.NewItems) p.PropertyChanged += OnPeerPropertyChanged;
        UpdateEmptyStates();
        UpdateListTitle();
    }

    private void OnPeerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Peer.IsOnline)) UpdateListTitle();
        if (e.PropertyName == nameof(Peer.SeenThisSession)) UpdateEmptyStates();
    }

    private void ShowInfo(string text)
    {
        InfoBar.Message = text;
        InfoBar.IsOpen = true;
        InfoBar.Visibility = Visibility.Visible;
        Anim.FadeIn(InfoBar, fromY: 12);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Anim.FadeOut(InfoBar, toY: 12);
        };
        timer.Start();
    }

    /// <summary>İçerik alanında görünümler arası yumuşak geçiş.</summary>
    private void ShowContent(UIElement target)
    {
        foreach (UIElement child in ContentHost.Children)
        {
            if (child == target) continue;
            child.BeginAnimation(OpacityProperty, null);
            child.Visibility = Visibility.Collapsed;
        }
        if (_currentContent != target || target.Visibility != Visibility.Visible)
            Anim.FadeIn(target, fromY: 10, ms: 240);
        _currentContent = target;
        if (target != Chat) _chat.ActiveConversation = null;
        UpdateListWidth();
    }

    private const double RailWidth = 56;
    private const double ContentMinWidth = 320;
    /// <summary>Bilgi paneli açıkken liste, panele bu kadar yer kalana dek daralır (sonra panel daralır).</summary>
    private const double InfoComfortWidth = 260;

    private const double ListMinWidth = 180;
    private const double ListMaxWidth = 400;

    /// <summary>
    /// Liste genişliği: kaydedilen değer; pencere daraldıkça içerik alanına yer kalsın diye kendiliğinden küçülür.
    /// Kaydedilen değer değişmez, pencere büyüyünce liste eski genişliğine döner.
    /// Üst sınır da pencereye bağlıdır: liste çizgiden sürüklenerek sohbet alanını (ve açık bilgi panelini) ezemez.
    /// </summary>
    private void UpdateListWidth()
    {
        var saved = Math.Clamp(App.Settings.ListWidth, ListMinWidth, ListMaxWidth);
        var available = ActualWidth - RailWidth;
        if (available <= 0)
        {
            ListColumn.Width = new GridLength(saved);
            return;
        }
        var required = ContentMinWidth + (_currentContent == Chat && Chat.IsInfoOpen ? InfoComfortWidth : 0);
        var max = Math.Clamp(available - required, ListMinWidth, ListMaxWidth);
        ListColumn.MaxWidth = max;
        var width = Math.Min(saved, max);
        if (Math.Abs(ListColumn.ActualWidth - width) > 0.5 || ListColumn.Width.IsStar) ListColumn.Width = new GridLength(width);
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        // İçerik sütunu yıldız genişlikte kalsın; yalnızca liste genişliğini sabitle ve kaydet.
        // GridSplitter iki sütunu da piksel genişliğe çevirir; pencere büyüyünce sohbet alanı genişlesin diye geri al.
        var width = Math.Round(ListColumn.ActualWidth);
        ListColumn.Width = new GridLength(width);
        ContentColumn.Width = new GridLength(1, GridUnitType.Star);
        App.Settings.ListWidth = width;
    }

    // ------------------------------------------------------------------ Sol ray

    private void OnRailChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || ConversationList == null) return;
        if (sender == SettingsTab)
        {
            ShowContent(Settings);
            ConversationList.SelectedItem = null;
            return;
        }

        var people = sender == PeopleTab;
        var show = people ? (UIElement)PeopleList : ConversationList;
        var hide = people ? (UIElement)ConversationList : PeopleList;
        hide.Visibility = Visibility.Collapsed;
        Anim.FadeIn(show, fromX: people ? 16 : -16, fromY: 0, ms: 220);
        RescanButton.Visibility = people ? Visibility.Visible : Visibility.Collapsed;
        if (_currentContent == Settings) ShowContent(_chat.ActiveConversation != null ? Chat : EmptyState);
        UpdateListTitle();
        UpdateEmptyStates();
    }

    private void OnProfileClicked(object sender, RoutedEventArgs e)
    {
        SettingsTab.IsChecked = true;
        Settings.FocusProfile();
    }

    private void OnShowPeopleClicked(object sender, RoutedEventArgs e) => PeopleTab.IsChecked = true;

    private async void OnRescanClicked(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        await _chat.RescanAsync();
        await Task.Delay(1500);
        RescanButton.IsEnabled = true;
    }

    // ------------------------------------------------------------------ Arama

    private string _peopleQuery = "";

    private bool PeopleFilter(object o) =>
        o is Peer p && p.SeenThisSession &&
        (_peopleQuery.Length == 0 || p.Name.Contains(_peopleQuery, StringComparison.CurrentCultureIgnoreCase));

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        _conversations.Filter = q.Length == 0
            ? null
            : o => o is Conversation c && c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        _peopleQuery = q;
        _people.Refresh();
        UpdateEmptyStates();
    }

    // ------------------------------------------------------------------ Sohbetler

    private void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (ConversationList.SelectedItem is Conversation conv) ShowChat(conv);
    }

    private void ShowChat(Conversation conv)
    {
        if (SettingsTab.IsChecked == true)
        {
            _suppressSelection = true;
            ChatsTab.IsChecked = true;
            _suppressSelection = false;
        }
        Chat.SetConversation(conv);
        ShowContent(Chat);
        _chat.ActiveConversation = conv;
        NotificationService.ClearFor(conv.Id);
    }

    /// <summary>Dışarıdan (bildirim tıklaması) bir sohbeti açar.</summary>
    public void OpenConversation(Conversation conv)
    {
        Dialogs.Visibility = Dialogs.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (ChatsTab.IsChecked != true) ChatsTab.IsChecked = true;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        _suppressSelection = true;
        ConversationList.SelectedItem = conv;
        _suppressSelection = false;
        ConversationList.ScrollIntoView(conv);
        ShowChat(conv);
    }

    private void OnPeerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PeopleList.SelectedItem is not Peer peer) return;
        var conv = _chat.OpenDirect(peer);
        // Seçimi temizle ki aynı kişiye tekrar tıklanabilsin.
        Dispatcher.BeginInvoke(() => PeopleList.SelectedItem = null);
        OpenConversation(conv);
    }

    // ------------------------------------------------------------------ Bağlam menüsü

    private void OnConversationRightClick(object sender, MouseButtonEventArgs e)
    {
        _menuTarget = e.OriginalSource is DependencyObject d ? FindAncestor<ListBoxItem>(d)?.DataContext as Conversation : null;
    }

    private void OnConversationMenuOpened(object sender, RoutedEventArgs e)
    {
        if (_menuTarget == null)
        {
            ((ContextMenu)sender).IsOpen = false;
            return;
        }
        PinItem.Header = _menuTarget.IsPinned ? "Sabitlemeyi kaldır" : "Sabitle";
        MuteItem.Header = _menuTarget.IsMuted ? "Sesi aç" : "Sessize al";
        MuteItemIcon.Symbol = _menuTarget.IsMuted ? SymbolRegular.Alert20 : SymbolRegular.AlertOff20;
        var peer = _menuTarget.IsGroup ? null : _menuTarget.Peer;
        BlockItem.Visibility = peer == null ? Visibility.Collapsed : Visibility.Visible;
        BlockItem.Header = peer?.IsBlocked == true ? "Engeli kaldır" : "Engelle";
        BlockItemIcon.Symbol = peer?.IsBlocked == true ? SymbolRegular.PersonAvailable20 : SymbolRegular.PersonProhibited20;
        UnreadItem.Visibility = _menuTarget.UnreadCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteItem.Header = _menuTarget.IsGroup && _menuTarget.Group is { } g && g.IsMember(_chat.MyId) && !g.Disbanded
            ? "Gruptan çık ve sil"
            : "Sohbeti sil";
    }

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        if (_menuTarget == null) return;
        _menuTarget.IsPinned = !_menuTarget.IsPinned;
        _chat.MarkDirty();
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        if (_menuTarget != null) _chat.SetMuted(_menuTarget, !_menuTarget.IsMuted);
    }

    private async void OnBlockClicked(object sender, RoutedEventArgs e)
    {
        if (_menuTarget?.Peer is not { } peer || _menuTarget.IsGroup) return;
        if (peer.IsBlocked)
        {
            _chat.SetBlocked(peer, false);
            return;
        }
        if (await Dialogs.ConfirmAsync($"{peer.Name} engellensin mi?",
                "Engellenen kişi size bire bir mesaj gönderemez ve ifade bırakamaz; siz de onun durumunu ve son aktifliğini görmezsiniz. Ortak gruplardaki mesajları görünmeye devam eder.",
                "Engelle", danger: true))
            _chat.SetBlocked(peer, true);
    }

    private void OnLockClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).Lock();

    // ------------------------------------------------------------------ Bildirim zili

    private async void OnBellClicked(object sender, RoutedEventArgs e)
    {
        // Windows bildirimleri kapalıyken uygulama içi sessize alma anlamsız: önce Windows ayarı açılmalı.
        if (NotificationService.SystemNotificationsDisabled)
        {
            if (Window.GetWindow(this) is MainWindow window) await window.ShowNotificationsOffAsync();
            return;
        }
        App.Settings.NotificationsMuted = !App.Settings.NotificationsMuted;
        Anim.PopIn(BellIcon, 180);
    }

    private void OnSystemNotificationSettingChanged() => Dispatcher.BeginInvoke(() =>
    {
        UpdateBell();
        Anim.PopIn(BellIcon, 180);
    });

    private void UpdateBell()
    {
        var systemOff = NotificationService.SystemNotificationsDisabled;
        var muted = App.Settings.NotificationsMuted;
        BellIcon.Symbol = systemOff || muted ? SymbolRegular.AlertOff24 : SymbolRegular.Alert24;
        var tip = systemOff ? "Windows bildirimleri kapalı — açmak için tıklayın"
            : muted ? "Bildirimler sessizde — açmak için tıklayın"
            : "Tüm bildirimleri sessize al";
        BellButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(BellButton, systemOff ? "Windows bildirimleri kapalı" : muted ? "Bildirimleri aç" : "Bildirimleri sessize al");
        BellButton.Foreground = systemOff ? (Brush)FindResource("SystemFillColorCriticalBrush")
            : muted ? (Brush)FindResource("SystemFillColorCautionBrush")
            : (Brush)FindResource("TextFillColorSecondaryBrush");
    }

    private void OnMarkUnreadClicked(object sender, RoutedEventArgs e)
    {
        if (_menuTarget == null) return;
        if (_chat.ActiveConversation == _menuTarget)
        {
            ConversationList.SelectedItem = null;
            ShowContent(EmptyState);
        }
        _chat.MarkConversationUnread(_menuTarget);
    }

    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        var conv = _menuTarget;
        if (conv == null) return;
        if (await Dialogs.ConfirmAsync("Sohbet temizlensin mi?", $"\"{conv.Title}\" sohbetindeki tüm mesajlar bu cihazdan silinecek.", "Temizle", danger: true))
            _chat.ClearConversation(conv);
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        var conv = _menuTarget;
        if (conv == null) return;
        await DeleteConversationAsync(conv);
    }

    public async Task DeleteConversationAsync(Conversation conv)
    {
        var leaving = conv.IsGroup && conv.Group is { } g && g.IsMember(_chat.MyId) && !g.Disbanded;
        var message = leaving
            ? $"\"{conv.Title}\" grubundan ayrılacak ve sohbet geçmişi bu cihazdan silinecek."
            : $"\"{conv.Title}\" sohbeti ve tüm mesajları bu cihazdan silinecek.";
        if (!await Dialogs.ConfirmAsync(leaving ? "Gruptan çıkılsın mı?" : "Sohbet silinsin mi?", message, leaving ? "Çık ve sil" : "Sil", danger: true))
            return;
        if (_chat.ActiveConversation == conv || Chat.Conversation == conv) ShowContent(EmptyState);
        _chat.DeleteConversation(conv);
    }

    // ------------------------------------------------------------------ Grup oluşturma

    private async void OnNewGroupClicked(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = "Grup adı", MaxLength = 48, Icon = new SymbolIcon(SymbolRegular.People24) };
        var desc = new TextBox { PlaceholderText = "Açıklama (isteğe bağlı)", MaxLength = 200, Margin = new Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(name, "Grup adı");
        System.Windows.Automation.AutomationProperties.SetName(desc, "Açıklama");
        var picker = new PeerPicker(_chat.Peers);
        var label = new System.Windows.Controls.TextBlock
        {
            Text = "Üyeler",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 18, 0, 8),
        };
        label.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var body = new StackPanel { Children = { name, desc, label, picker } };

        var ok = await Dialogs.ShowAsync("Yeni grup", body, "Oluştur",
            canSubmit: () => name.Text.Trim().Length > 0 && picker.Selected.Count > 0,
            bindRefresh: refresh =>
            {
                name.TextChanged += (_, _) => refresh();
                picker.SelectionChanged += refresh;
            });
        if (!ok) return;

        var conv = _chat.CreateGroup(name.Text, desc.Text, picker.Selected);
        OpenConversation(conv);
    }

    public Task<bool> ConfirmAsync(string title, string message, string primary, bool danger = false) =>
        Dialogs.ConfirmAsync(title, message, primary, danger);

    public DialogHost DialogHost => Dialogs;

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
